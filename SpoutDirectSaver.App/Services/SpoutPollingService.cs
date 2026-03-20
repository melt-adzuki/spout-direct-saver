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
    private const int ReceiveBufferCount = 4;
    private readonly object _startGate = new();
    private readonly object _previewGate = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;
    private IntPtr _previewFrontBuffer = IntPtr.Zero;
    private IntPtr _previewBackBuffer = IntPtr.Zero;
    private int _previewBufferLength;
    private LivePreviewFrame _latestPreviewFrame;
    private bool _hasPreviewFrame;

    public event EventHandler<FramePacket>? FrameArrived;

    public event EventHandler<CaptureStatus>? StatusChanged;

    public bool TryReadLatestPreviewFrame(Action<LivePreviewFrame> reader)
    {
        lock (_previewGate)
        {
            if (!_hasPreviewFrame || _previewFrontBuffer == IntPtr.Zero)
            {
                return false;
            }

            reader(_latestPreviewFrame);
            return true;
        }
    }

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

        ReleasePreviewBuffers();
    }

    private void RunPollingLoop(CancellationToken cancellationToken)
    {
        using var schedulingScope = WindowsScheduling.EnterCaptureProfile();
        using var receiver = new SpoutReceiver();

        if (!receiver.CreateOpenGL())
        {
            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout OpenGL コンテキストの初期化に失敗しました。"));
            return;
        }

        bool wasConnected = false;
        string senderName = string.Empty;
        uint width = 0;
        uint height = 0;
        var lastAcceptedSenderFrame = -1;
        long nextPollTicks = Stopwatch.GetTimestamp();

        try
        {
            receiver.SetCPUmode(false);
            receiver.BufferMode = true;
            receiver.Buffers = ReceiveBufferCount;
            receiver.SetFrameCount(true);

            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender を待っています。"));

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!TryReceivePreviewImage(
                        receiver,
                        senderName,
                        width,
                        height,
                        out var receivedSenderName,
                        out var receivedWidth,
                        out var receivedHeight,
                        out var receiveMessage))
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

                var senderWasUpdated =
                    !wasConnected ||
                    receiver.IsUpdated ||
                    receivedWidth != width ||
                    receivedHeight != height ||
                    !string.Equals(receivedSenderName, senderName, StringComparison.Ordinal);

                if (senderWasUpdated)
                {
                    width = receivedWidth;
                    height = receivedHeight;
                    senderName = receivedSenderName;

                    var message = wasConnected
                        ? $"sender の状態が更新されました: {width} x {height}"
                        : $"Spout sender \"{senderName}\" に接続しました。";

                    wasConnected = true;
                    lastAcceptedSenderFrame = -1;

                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        receiver.SenderFps,
                        receiveMessage ?? message));

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var senderFrame = receiver.SenderFrame;
                if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                lastAcceptedSenderFrame = senderFrame;
                PublishPreviewFrame(width, height, senderName, receiver.SenderFps, Stopwatch.GetTimestamp());

                var frameHandler = FrameArrived;
                if (frameHandler is not null)
                {
                    var frameCopy = GC.AllocateUninitializedArray<byte>(_previewBufferLength);
                    lock (_previewGate)
                    {
                        Marshal.Copy(_previewFrontBuffer, frameCopy, 0, _previewBufferLength);
                    }

                    frameHandler.Invoke(this, new FramePacket(
                        frameCopy,
                        width,
                        height,
                        senderName,
                        receiver.SenderFps,
                        _latestPreviewFrame.StopwatchTicks,
                        DateTimeOffset.UtcNow));
                }

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
        return receiver.ReceiveTexture();
    }

    private bool TryReceivePreviewImage(
        SpoutReceiver receiver,
        string senderName,
        uint width,
        uint height,
        out string receivedSenderName,
        out uint receivedWidth,
        out uint receivedHeight,
        out string? message)
    {
        unsafe
        {
            var senderNameBytes = new byte[256];
            if (!string.IsNullOrWhiteSpace(senderName))
            {
                var encodedName = Encoding.ASCII.GetBytes(senderName);
                Array.Copy(encodedName, senderNameBytes, Math.Min(encodedName.Length, senderNameBytes.Length - 1));
            }

            receivedWidth = width;
            receivedHeight = height;

            fixed (byte* senderNamePtr = senderNameBytes)
            {
                var pixelBuffer = _previewBackBuffer == IntPtr.Zero ? null : (byte*)_previewBackBuffer;
                var received = receiver.ReceiveImage(
                    (sbyte*)senderNamePtr,
                    ref receivedWidth,
                    ref receivedHeight,
                    pixelBuffer,
                    0x80E1u,
                    false,
                    0);

                receivedSenderName = ReadNullTerminatedAscii(senderNameBytes);
                if (!received)
                {
                    message = null;
                    return false;
                }
            }
        }

        if (receivedWidth == 0 || receivedHeight == 0)
        {
            message = null;
            return false;
        }

        var requiredLength = checked((int)(receivedWidth * receivedHeight * 4));
        if (_previewBackBuffer == IntPtr.Zero || _previewBufferLength != requiredLength)
        {
            EnsurePreviewBufferSize(requiredLength);
            message = $"受信サイズ {receivedWidth} x {receivedHeight} に合わせてバッファを再初期化しました。";
            return false;
        }

        message = null;
        return true;
    }

    private static string ReadNullTerminatedAscii(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    private void RaiseStatus(CaptureStatus status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void EnsurePreviewBufferSize(int requiredBufferLength)
    {
        lock (_previewGate)
        {
            if (_previewFrontBuffer != IntPtr.Zero && _previewBufferLength == requiredBufferLength)
            {
                return;
            }

            ReleasePreviewBuffersUnsafe();
            _previewFrontBuffer = Marshal.AllocHGlobal(requiredBufferLength);
            _previewBackBuffer = Marshal.AllocHGlobal(requiredBufferLength);
            _previewBufferLength = requiredBufferLength;
            _hasPreviewFrame = false;
        }
    }

    private void PublishPreviewFrame(uint width, uint height, string senderName, double senderFps, long stopwatchTicks)
    {
        lock (_previewGate)
        {
            (_previewFrontBuffer, _previewBackBuffer) = (_previewBackBuffer, _previewFrontBuffer);
            _latestPreviewFrame = new LivePreviewFrame(
                _previewFrontBuffer,
                width,
                height,
                senderName,
                senderFps,
                stopwatchTicks);
            _hasPreviewFrame = true;
        }
    }

    private void ReleasePreviewBuffers()
    {
        lock (_previewGate)
        {
            ReleasePreviewBuffersUnsafe();
        }
    }

    private void ReleasePreviewBuffersUnsafe()
    {
        if (_previewFrontBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_previewFrontBuffer);
            _previewFrontBuffer = IntPtr.Zero;
        }

        if (_previewBackBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_previewBackBuffer);
            _previewBackBuffer = IntPtr.Zero;
        }

        _previewBufferLength = 0;
        _hasPreviewFrame = false;
        _latestPreviewFrame = default;
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
