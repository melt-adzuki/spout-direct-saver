using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spout.Interop;
using Spout.NETCore;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class SpoutPollingService : IAsyncDisposable
{
    private const uint ReceivePixelFormat = 0x80E1;
    private readonly object _startGate = new();

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

        IntPtr receiveBuffer = IntPtr.Zero;
        int receiveBufferLength = 0;
        bool wasConnected = false;
        string senderName = string.Empty;
        uint width = 0;
        uint height = 0;
        long nextPollTicks = Stopwatch.GetTimestamp();

        try
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender を待っています。"));

            while (!cancellationToken.IsCancellationRequested)
            {
                var stateReady = receiver.ReceiveTexture();
                if (!stateReady && !receiver.IsConnected)
                {
                    if (wasConnected)
                    {
                        RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender との接続が切れました。"));
                        wasConnected = false;
                        senderName = string.Empty;
                        width = 0;
                        height = 0;
                        ReleaseReceiveBuffer(ref receiveBuffer, ref receiveBufferLength);
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var senderWidth = receiver.SenderWidth;
                var senderHeight = receiver.SenderHeight;
                if (senderWidth == 0 || senderHeight == 0)
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var requiredBufferLength = checked((int)(senderWidth * senderHeight * 4));
                var senderWasUpdated =
                    receiveBuffer == IntPtr.Zero ||
                    !wasConnected ||
                    !stateReady ||
                    receiver.IsUpdated ||
                    senderWidth != width ||
                    senderHeight != height ||
                    receiveBufferLength != requiredBufferLength;

                if (senderWasUpdated)
                {
                    width = senderWidth;
                    height = senderHeight;
                    EnsureReceiveBufferSize(requiredBufferLength, ref receiveBuffer, ref receiveBufferLength);

                    var connectedSenderName = receiver.SenderName;
                    var message = wasConnected
                        ? $"sender の状態が更新されました: {width} x {height}"
                        : $"Spout sender \"{connectedSenderName}\" に接続しました。";

                    senderName = connectedSenderName;
                    wasConnected = true;

                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        receiver.SenderFps,
                        message));

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                if (!receiver.IsFrameNew)
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                unsafe
                {
                    var receiveSenderName = string.IsNullOrWhiteSpace(senderName)
                        ? receiver.SenderName
                        : senderName;
                    var senderNameBytes = Encoding.ASCII.GetBytes($"{receiveSenderName}\0");
                    var receiveWidth = width;
                    var receiveHeight = height;
                    var pixels = (byte*)receiveBuffer;

                    fixed (byte* senderNamePtr = senderNameBytes)
                    {
                        var received = receiver.ReceiveImage(
                            (sbyte*)senderNamePtr,
                            ref receiveWidth,
                            ref receiveHeight,
                            pixels,
                            ReceivePixelFormat,
                            true,
                            0);
                        if (!received)
                        {
                            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout フレーム受信に失敗しました。sender を再待機します。"));
                            wasConnected = false;
                            senderName = string.Empty;
                            width = 0;
                            height = 0;
                            ReleaseReceiveBuffer(ref receiveBuffer, ref receiveBufferLength);
                            WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                            continue;
                        }

                        if (receiveWidth != width || receiveHeight != height)
                        {
                            width = receiveWidth;
                            height = receiveHeight;
                            ReleaseReceiveBuffer(ref receiveBuffer, ref receiveBufferLength);
                            WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                            continue;
                        }
                    }
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

                var frameCopy = GC.AllocateUninitializedArray<byte>(receiveBufferLength);
                Marshal.Copy(receiveBuffer, frameCopy, 0, receiveBufferLength);

                FrameArrived?.Invoke(this, new FramePacket(
                    frameCopy,
                    width,
                    height,
                    senderName,
                    receiver.SenderFps,
                    Stopwatch.GetTimestamp(),
                    DateTimeOffset.UtcNow));
                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            ReleaseReceiveBuffer(ref receiveBuffer, ref receiveBufferLength);
            receiver.ReleaseReceiver();
            receiver.CloseOpenGL();
        }
    }

    private void RaiseStatus(CaptureStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private static void EnsureReceiveBufferSize(int requiredBufferLength, ref IntPtr receiveBuffer, ref int receiveBufferLength)
    {
        if (receiveBuffer != IntPtr.Zero && receiveBufferLength == requiredBufferLength)
        {
            return;
        }

        ReleaseReceiveBuffer(ref receiveBuffer, ref receiveBufferLength);
        receiveBuffer = Marshal.AllocHGlobal(requiredBufferLength);
        receiveBufferLength = requiredBufferLength;
    }

    private static void ReleaseReceiveBuffer(ref IntPtr receiveBuffer, ref int receiveBufferLength)
    {
        if (receiveBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(receiveBuffer);
            receiveBuffer = IntPtr.Zero;
        }

        receiveBufferLength = 0;
    }

    private static void WaitUntilNextPoll(ref long nextPollTicks, double senderFps, CancellationToken cancellationToken)
    {
        nextPollTicks += GetPollingIntervalTicks(senderFps);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remainingTicks = nextPollTicks - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs >= 2.0)
            {
                Thread.Sleep(1);
                continue;
            }

            Thread.SpinWait(128);
        }
    }

    private static long GetPollingIntervalTicks(double senderFps)
    {
        var targetFps = senderFps > 1.0
            ? Math.Min(senderFps, 120.0)
            : 120.0;
        return (long)Math.Round(Stopwatch.Frequency / targetFps);
    }
}
