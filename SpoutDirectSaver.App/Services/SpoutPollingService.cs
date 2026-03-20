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
    private int _recordingModeEnabled;

    public event EventHandler<FramePacket>? FrameArrived;

    public event EventHandler<CaptureStatus>? StatusChanged;

    public void SetRecordingMode(bool enabled)
    {
        Interlocked.Exchange(ref _recordingModeEnabled, enabled ? 1 : 0);
    }

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
        using var sharedTextureReader = new D3D11SpoutSharedTextureReader();
        using var frameCounter = new SpoutFrameCount();

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
        var frameCountSenderName = string.Empty;
        var frameCountSupportProbed = false;
        var frameCountSupported = false;

        try
        {
            receiver.SetCPUmode(true);
            receiver.BufferMode = false;
            receiver.Buffers = ReceiveBufferCount;
            receiver.SetFrameCount(true);
            frameCounter.SetFrameCount(true);

            RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender を待っています。"));

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!TryEnsureReceiverConnected(
                        receiver,
                        senderName,
                        out var connectedSenderName,
                        out var connectedWidth,
                        out var connectedHeight))
                {
                    if (wasConnected)
                    {
                        RaiseStatus(new CaptureStatus(false, string.Empty, 0, 0, 0, "Spout sender との接続が切れました。"));
                        wasConnected = false;
                        senderName = string.Empty;
                        width = 0;
                        height = 0;
                        lastAcceptedSenderFrame = -1;
                        frameCountSenderName = string.Empty;
                        frameCountSupportProbed = false;
                        frameCountSupported = false;
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var senderWasUpdated =
                    !wasConnected ||
                    receiver.IsUpdated ||
                    connectedWidth != width ||
                    connectedHeight != height ||
                    !string.Equals(connectedSenderName, senderName, StringComparison.Ordinal);

                var requiredLength = checked((int)(connectedWidth * connectedHeight * 4));
                if (_previewBackBuffer == IntPtr.Zero || _previewBufferLength != requiredLength)
                {
                    EnsurePreviewBufferSize(requiredLength);
                    senderWasUpdated = true;
                }

                var preferSharedTextureReadback = TryPrepareSharedTextureReadback(
                    receiver,
                    sharedTextureReader,
                    frameCounter,
                    connectedSenderName,
                    ref frameCountSenderName,
                    out var effectiveSenderFps);
                if (preferSharedTextureReadback && !frameCountSupportProbed)
                {
                    frameCountSupportProbed = true;
                    frameCountSupported =
                        !string.IsNullOrWhiteSpace(connectedSenderName) &&
                        frameCounter.WaitNewFrame(0) &&
                        frameCounter.SenderFrame > 0;
                }

                preferSharedTextureReadback &= frameCountSupported;

                if (preferSharedTextureReadback)
                {
                    if (!TryReceiveSharedTextureFrame(
                            sharedTextureReader,
                            frameCounter,
                            connectedSenderName,
                            ref frameCountSupportProbed,
                            ref frameCountSupported,
                            _previewBackBuffer,
                            _previewBufferLength,
                            effectiveSenderFps,
                            cancellationToken))
                    {
                        continue;
                    }
                }
                else if (!TryReceivePreviewImage(
                             receiver,
                             connectedSenderName,
                             connectedWidth,
                             connectedHeight))
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                if (senderWasUpdated)
                {
                    width = connectedWidth;
                    height = connectedHeight;
                    senderName = connectedSenderName;

                    var message = wasConnected
                        ? $"sender の状態が更新されました: {width} x {height}"
                        : $"Spout sender \"{senderName}\" に接続しました。";

                    wasConnected = true;
                    lastAcceptedSenderFrame = -1;
                    frameCountSupportProbed = false;
                    frameCountSupported = false;

                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        effectiveSenderFps,
                        message));

                    if (!preferSharedTextureReadback)
                    {
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    }
                    continue;
                }

                var senderFrame = preferSharedTextureReadback
                    ? frameCounter.SenderFrame
                    : receiver.SenderFrame;
                if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
                {
                    if (!preferSharedTextureReadback)
                    {
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    }
                    continue;
                }

                lastAcceptedSenderFrame = senderFrame;
                var acceptedFrameTicks = Stopwatch.GetTimestamp();
                var recordingMode = Volatile.Read(ref _recordingModeEnabled) != 0;
                if (!recordingMode)
                {
                    PublishPreviewFrame(width, height, senderName, effectiveSenderFps, acceptedFrameTicks);
                }

                var frameHandler = FrameArrived;
                if (frameHandler is not null)
                {
                    var frameCopy = PixelBufferLease.Rent(_previewBufferLength);
                    var sourceBuffer = recordingMode ? _previewBackBuffer : _previewFrontBuffer;
                    if (!recordingMode)
                    {
                        lock (_previewGate)
                        {
                            Marshal.Copy(sourceBuffer, frameCopy.Buffer, 0, _previewBufferLength);
                        }
                    }
                    else
                    {
                        Marshal.Copy(sourceBuffer, frameCopy.Buffer, 0, _previewBufferLength);
                    }

                    frameHandler.Invoke(this, new FramePacket(
                        frameCopy,
                        width,
                        height,
                        senderName,
                        effectiveSenderFps,
                        acceptedFrameTicks,
                        DateTimeOffset.UtcNow));
                }

                if (!preferSharedTextureReadback)
                {
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                }
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

    private static bool TryEnsureReceiverConnected(
        SpoutReceiver receiver,
        string requestedSenderName,
        out string connectedSenderName,
        out uint width,
        out uint height)
    {
        unsafe
        {
            var senderNameBytes = new byte[256];
            if (!string.IsNullOrWhiteSpace(requestedSenderName))
            {
                var encodedName = Encoding.ASCII.GetBytes(requestedSenderName);
                Array.Copy(encodedName, senderNameBytes, Math.Min(encodedName.Length, senderNameBytes.Length - 1));
            }

            width = 0;
            height = 0;

            fixed (byte* senderNamePtr = senderNameBytes)
            {
                bool ready;
                if (!receiver.IsConnected)
                {
                    ready = receiver.CreateReceiver((sbyte*)senderNamePtr, ref width, ref height);
                }
                else
                {
                    var connected = receiver.IsConnected;
                    ready = receiver.CheckReceiver((sbyte*)senderNamePtr, ref width, ref height, ref connected) && connected;
                }

                connectedSenderName = ReadNullTerminatedAscii(senderNameBytes);
                return ready && width > 0 && height > 0;
            }
        }
    }

    private unsafe bool TryReceivePreviewImage(
        SpoutReceiver receiver,
        string senderName,
        uint width,
        uint height)
    {
        var senderNameBytes = new byte[256];
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            var encodedName = Encoding.ASCII.GetBytes(senderName);
            Array.Copy(encodedName, senderNameBytes, Math.Min(encodedName.Length, senderNameBytes.Length - 1));
        }

        var receivedWidth = width;
        var receivedHeight = height;

        fixed (byte* senderNamePtr = senderNameBytes)
        {
            return receiver.ReceiveImage(
                (sbyte*)senderNamePtr,
                ref receivedWidth,
                ref receivedHeight,
                (byte*)_previewBackBuffer,
                0x80E1u,
                false,
                0);
        }
    }

    private static bool TryPrepareSharedTextureReadback(
        SpoutReceiver receiver,
        D3D11SpoutSharedTextureReader sharedTextureReader,
        SpoutFrameCount frameCounter,
        string senderName,
        ref string frameCountSenderName,
        out double effectiveSenderFps)
    {
        effectiveSenderFps = receiver.SenderFps;
        if (!receiver.SenderGLDX || receiver.SenderCPU || receiver.SenderHandle == IntPtr.Zero)
        {
            return false;
        }

        if (!sharedTextureReader.TrySynchronizeSender(receiver, senderName, out _))
        {
            return false;
        }

        if (!string.Equals(frameCountSenderName, senderName, StringComparison.Ordinal))
        {
            frameCounter.CleanupFrameCount();
            frameCounter.EnableFrameCount(senderName);
            frameCountSenderName = senderName;
        }

        if (frameCounter.SenderFps > 1.0)
        {
            effectiveSenderFps = frameCounter.SenderFps;
        }

        return true;
    }

    private static bool TryReceiveSharedTextureFrame(
        D3D11SpoutSharedTextureReader sharedTextureReader,
        SpoutFrameCount frameCounter,
        string senderName,
        ref bool frameCountSupportProbed,
        ref bool frameCountSupported,
        IntPtr destinationBuffer,
        int destinationLength,
        double senderFps,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryAwaitSharedFrame(
                frameCounter,
                senderName,
                ref frameCountSupportProbed,
                ref frameCountSupported,
                senderFps))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return sharedTextureReader.TryReadFrame(destinationBuffer, destinationLength, out _);
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

    private static bool WaitForNextFrame(SpoutFrameCount frameCounter, double senderFps)
    {
        var timeoutMilliseconds = senderFps > 1.0
            ? (uint)Math.Clamp((int)Math.Ceiling((1000.0 / Math.Min(senderFps, 120.0)) * 2.0), 4, 67)
            : 20u;
        return frameCounter.WaitNewFrame(timeoutMilliseconds);
    }

    private static bool TryAwaitSharedFrame(
        SpoutFrameCount frameCounter,
        string senderName,
        ref bool frameCountSupportProbed,
        ref bool frameCountSupported,
        double senderFps)
    {
        if (!frameCountSupportProbed)
        {
            frameCountSupportProbed = true;
            frameCountSupported =
                !string.IsNullOrWhiteSpace(senderName) &&
                frameCounter.WaitNewFrame(0) &&
                frameCounter.SenderFrame > 0;
            return true;
        }

        if (!frameCountSupported)
        {
            return true;
        }

        return WaitForNextFrame(frameCounter, senderFps);
    }
}
