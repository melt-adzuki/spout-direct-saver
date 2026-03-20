using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Spout.Interop;
using Spout.NETCore;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class SpoutPollingService : IAsyncDisposable
{
    private const int ReceiveBufferCount = 4;
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
        using var sharedTextureReader = new D3D11SpoutSharedTextureReader();

        if (!receiver.CreateOpenGL())
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout OpenGL コンテキストの初期化に失敗しました。"));
            return;
        }

        receiver.CPUmode = true;
        receiver.BufferMode = true;
        receiver.Buffers = ReceiveBufferCount;
        receiver.SetFrameCount(true);

        bool wasConnected = false;
        string senderName = string.Empty;
        uint width = 0;
        uint height = 0;
        var lastAcceptedSenderFrame = -1;
        long nextPollTicks = Stopwatch.GetTimestamp();

        try
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender を待っています。"));

            while (!cancellationToken.IsCancellationRequested)
            {
                var stateReady = PrepareReceive(receiver);
                if (!stateReady && !receiver.IsConnected)
                {
                    if (wasConnected)
                    {
                        RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender との接続が切れました。"));
                        wasConnected = false;
                        senderName = string.Empty;
                        width = 0;
                        height = 0;
                        lastAcceptedSenderFrame = -1;
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

                var senderWasUpdated =
                    !wasConnected ||
                    !stateReady ||
                    receiver.IsUpdated ||
                    senderWidth != width ||
                    senderHeight != height;

                if (senderWasUpdated)
                {
                    width = senderWidth;
                    height = senderHeight;

                    var connectedSenderName = receiver.SenderName;
                    if (!sharedTextureReader.TrySynchronizeSender(receiver, connectedSenderName, out var syncError))
                    {
                        RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, syncError ?? "共有テクスチャの初期化に失敗しました。"));
                        wasConnected = false;
                        senderName = string.Empty;
                        width = 0;
                        height = 0;
                        lastAcceptedSenderFrame = -1;
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                        continue;
                    }

                    var message = wasConnected
                        ? $"sender の状態が更新されました: {width} x {height}"
                        : $"Spout sender \"{connectedSenderName}\" に接続しました。";

                    senderName = connectedSenderName;
                    wasConnected = true;
                    lastAcceptedSenderFrame = -1;

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

                if (!sharedTextureReader.TrySynchronizeSender(receiver, senderName, out var resyncError))
                {
                    RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, resyncError ?? "共有テクスチャへの再接続に失敗しました。"));
                    wasConnected = false;
                    senderName = string.Empty;
                    width = 0;
                    height = 0;
                    lastAcceptedSenderFrame = -1;
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                if (!string.Equals(senderName, receiver.SenderName, StringComparison.Ordinal))
                {
                    senderName = receiver.SenderName;
                    lastAcceptedSenderFrame = -1;
                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        receiver.SenderFps,
                        $"受信 sender が \"{senderName}\" に切り替わりました。"));
                }

                var senderFrame = receiver.SenderFrame;
                if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var frameLength = checked((int)(width * height * 4));
                var frameCopy = GC.AllocateUninitializedArray<byte>(frameLength);
                if (!sharedTextureReader.TryReadFrame(frameCopy, out var readError))
                {
                    if (!string.IsNullOrWhiteSpace(readError))
                    {
                        RaiseStatus(new CaptureStatus(true, senderName, width, height, receiver.SenderFps, readError));
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                lastAcceptedSenderFrame = senderFrame;

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
            receiver.ReleaseReceiver();
            receiver.CloseOpenGL();
        }
    }

    private static bool PrepareReceive(SpoutReceiver receiver)
    {
        return receiver.IsConnected ? true : receiver.ReceiveTexture();
    }

    private void RaiseStatus(CaptureStatus status)
    {
        StatusChanged?.Invoke(this, status);
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
