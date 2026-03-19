using System;
using System.Threading;
using System.Threading.Tasks;
using Spout.Interop;
using Spout.NETCore;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class SpoutPollingService : IAsyncDisposable
{
    private readonly object _startGate = new();
    private const int PollingIntervalMs = (int)(1000.0 / 120.0);

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;

    public event EventHandler<FramePacket>? FrameArrived;

    public event EventHandler<CaptureStatus>? StatusChanged;

    public Task StartAsync()
    {
        lock (_startGate)
        {
            if (_workerTask is not null)
            {
                return Task.CompletedTask;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _workerTask = Task.Factory.StartNew(
                () => RunPollingLoop(_cancellationTokenSource.Token),
                _cancellationTokenSource.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task? workerTask;
        CancellationTokenSource? cancellationTokenSource;

        lock (_startGate)
        {
            workerTask = _workerTask;
            cancellationTokenSource = _cancellationTokenSource;
            _workerTask = null;
            _cancellationTokenSource = null;
        }

        if (cancellationTokenSource is not null)
        {
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        if (workerTask is not null)
        {
            try
            {
                await workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
        }
    }

    private void RunPollingLoop(CancellationToken cancellationToken)
    {
        using var receiver = new SpoutReceiver();

        if (!receiver.CreateOpenGL())
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout OpenGL コンテキストの初期化に失敗しました。"));
            return;
        }

        byte[]? receiveBuffer = null;
        bool wasConnected = false;
        string senderName = string.Empty;
        uint width = 0;
        uint height = 0;

        try
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender を待っています。"));

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!receiver.IsConnected || width == 0 || height == 0)
                {
                    var connected = receiver.ReceiveTexture();
                    if (!connected && !receiver.IsConnected)
                    {
                        if (wasConnected)
                        {
                            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender との接続が切れました。"));
                            wasConnected = false;
                            senderName = string.Empty;
                            width = 0;
                            height = 0;
                            receiveBuffer = null;
                        }

                        Thread.Sleep(PollingIntervalMs);
                        continue;
                    }
                    else if (connected || receiver.IsConnected)
                    {
                        // Immediately sync state to prevent re-entry
                        width = receiver.SenderWidth;
                        height = receiver.SenderHeight;
                        if (width == 0 || height == 0)
                        {
                            Thread.Sleep(PollingIntervalMs);
                            continue;
                        }
                        receiveBuffer = new byte[checked((int)(width * height * 4))];
                        senderName = receiver.SenderName;
                        wasConnected = true;
                        
                        RaiseStatus(new CaptureStatus(
                            true,
                            senderName,
                            width,
                            height,
                            receiver.SenderFps,
                            $"Spout sender \"{senderName}\" に接続しました。"));
                    }
                }

                if (receiveBuffer is null)
                {
                    Thread.Sleep(PollingIntervalMs);
                    continue;
                }

                unsafe
                {
                    fixed (byte* pixels = receiveBuffer)
                    {
                        var received = receiver.ReceiveImage(pixels, GLFormats.RGBA, true, 0);
                        if (!received)
                        {
                            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout フレーム受信に失敗しました。sender を再待機します。"));
                            wasConnected = false;
                            senderName = string.Empty;
                            width = 0;
                            height = 0;
                            receiveBuffer = null;
                            Thread.Sleep(PollingIntervalMs);
                            continue;
                        }
                    }
                }

                if (receiver.IsUpdated || receiver.SenderWidth != width || receiver.SenderHeight != height)
                {
                    width = receiver.SenderWidth;
                    height = receiver.SenderHeight;
                    receiveBuffer = new byte[checked((int)(width * height * 4))];

                    RaiseStatus(new CaptureStatus(
                        true,
                        receiver.SenderName,
                        width,
                        height,
                        receiver.SenderFps,
                        $"sender の解像度が更新されました: {width} x {height}"));

                    Thread.Sleep(PollingIntervalMs);
                    continue;
                }

                if (!receiver.IsFrameNew)
                {
                    Thread.Sleep(PollingIntervalMs);
                    continue;
                }

                if (!string.Equals(senderName, receiver.SenderName, StringComparison.Ordinal))
                {
                    senderName = receiver.SenderName;
                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        receiver.SenderFps,
                        $"受信 sender が \"{senderName}\" に切り替わりました。"));
                }

                var frameCopy = GC.AllocateUninitializedArray<byte>(receiveBuffer.Length);
                Buffer.BlockCopy(receiveBuffer, 0, frameCopy, 0, receiveBuffer.Length);

                FrameArrived?.Invoke(this, new FramePacket(
                    frameCopy,
                    width,
                    height,
                    senderName,
                    receiver.SenderFps,
                    DateTimeOffset.UtcNow));

                Thread.Sleep(PollingIntervalMs);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            receiver.ReleaseReceiver();
            receiver.CloseOpenGL();
        }
    }

    private void RaiseStatus(CaptureStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }
}
