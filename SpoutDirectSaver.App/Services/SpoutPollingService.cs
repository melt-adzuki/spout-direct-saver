using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Spout.Interop;
using Spout.NETCore;
using SpoutDirectSaver.App.Models;
using Vortice.DXGI;

namespace SpoutDirectSaver.App.Services;

internal sealed class SpoutPollingService : IAsyncDisposable
{
    private const int ReceiveBufferCount = 4;
    private static readonly long GpuRecordingWarmupTicks = Stopwatch.Frequency / 2;
    private readonly object _startGate = new();
    private readonly object _previewGate = new();
    private readonly long _previewIntervalTicks = ResolvePreviewIntervalTicks();

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;
    private IntPtr _previewFrontBuffer = IntPtr.Zero;
    private IntPtr _previewBackBuffer = IntPtr.Zero;
    private int _previewBufferLength;
    private LivePreviewFrame _latestPreviewFrame;
    private bool _hasPreviewFrame;
    private int _recordingModeEnabled;
    private int _gpuRecordingModeEnabled;
    private long _nextPreviewCaptureTicks;

    public event EventHandler<FramePacket>? FrameArrived;

    public event EventHandler<CaptureStatus>? StatusChanged;

    public void SetRecordingMode(bool enabled, bool preferGpuFrames)
    {
        DebugTrace.WriteLine(
            "SpoutPollingService",
            $"SetRecordingMode enabled={enabled} preferGpuFrames={preferGpuFrames}");
        Interlocked.Exchange(ref _recordingModeEnabled, enabled ? 1 : 0);
        Interlocked.Exchange(ref _gpuRecordingModeEnabled, enabled && preferGpuFrames ? 1 : 0);
        if (enabled)
        {
            lock (_previewGate)
            {
                _hasPreviewFrame = false;
            }
        }
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
        var currentReceiveConfiguration = ReceiveConfiguration.ImageFallback;
        var gpuCaptureFallbackWarned = false;
        var lastRecordingDiagnostic = string.Empty;
        var previousRecordingMode = false;
        var gpuRecordingRequestedTicks = 0L;
        var frameCountSenderName = string.Empty;
        var frameCountSupportProbed = false;
        var frameCountSupported = false;

        try
        {
            ApplyReceiveConfiguration(receiver, ReceiveConfiguration.ImageFallback);
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
                        gpuCaptureFallbackWarned = false;
                        _nextPreviewCaptureTicks = 0;
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var senderWasUpdated =
                    !wasConnected ||
                    connectedWidth != width ||
                    connectedHeight != height ||
                    !string.Equals(connectedSenderName, senderName, StringComparison.Ordinal);

                var desiredConfiguration = IsSharedTextureCapable(receiver)
                    ? ReceiveConfiguration.SharedTexturePreferred
                    : ReceiveConfiguration.ImageFallback;
                if (desiredConfiguration != currentReceiveConfiguration)
                {
                    ApplyReceiveConfiguration(receiver, desiredConfiguration);
                    currentReceiveConfiguration = desiredConfiguration;
                    senderWasUpdated = true;
                }

                var useSharedTextureCapture = TryPrepareSharedTextureReadback(
                    receiver,
                    sharedTextureReader,
                    connectedSenderName,
                    out var effectiveSenderFps);

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
                    gpuCaptureFallbackWarned = false;
                    _nextPreviewCaptureTicks = 0;
                    frameCountSupportProbed = false;
                    frameCountSupported = false;

                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        effectiveSenderFps,
                        message));
                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var frameHandler = FrameArrived;
                var recordingMode = Volatile.Read(ref _recordingModeEnabled) != 0;
                var gpuRecordingMode = recordingMode && Volatile.Read(ref _gpuRecordingModeEnabled) != 0;
                if (recordingMode && !previousRecordingMode)
                {
                    lastAcceptedSenderFrame = -1;
                    gpuRecordingRequestedTicks = Stopwatch.GetTimestamp();
                    gpuCaptureFallbackWarned = false;
                    lastRecordingDiagnostic = string.Empty;
                    DebugTrace.WriteLine(
                        "SpoutPollingService",
                        $"recording transition sender={connectedSenderName} gpuRequested={gpuRecordingMode}");
                }

                previousRecordingMode = recordingMode;

                if (recordingMode && frameHandler is null)
                {
                    EmitRecordingDiagnostic(
                        ref lastRecordingDiagnostic,
                        "recording enabled but FrameArrived handler is null");
                }

                if (gpuRecordingMode && !useSharedTextureCapture)
                {
                    var nowTicks = Stopwatch.GetTimestamp();
                    if (gpuRecordingRequestedTicks != 0 &&
                        nowTicks - gpuRecordingRequestedTicks >= GpuRecordingWarmupTicks)
                    {
                        Interlocked.Exchange(ref _gpuRecordingModeEnabled, 0);
                        gpuRecordingMode = false;
                        lastAcceptedSenderFrame = -1;
                        lastRecordingDiagnostic = string.Empty;
                        if (!gpuCaptureFallbackWarned)
                        {
                            gpuCaptureFallbackWarned = true;
                            DebugTrace.WriteLine(
                                "SpoutPollingService",
                                $"gpu warmup timeout -> cpu fallback sender={connectedSenderName} size={connectedWidth}x{connectedHeight}");
                            RaiseStatus(new CaptureStatus(
                                true,
                                connectedSenderName,
                                connectedWidth,
                                connectedHeight,
                                effectiveSenderFps,
                                "GPU shared texture 録画経路を開始できなかったため、CPU 受信へフォールバックします。"));
                        }
                    }
                }

                if (useSharedTextureCapture &&
                    !TryAwaitSharedFrame(frameCounter, connectedSenderName, ref frameCountSenderName, ref frameCountSupportProbed, ref frameCountSupported, receiver.SenderFps))
                {
                    if (recordingMode)
                    {
                        EmitRecordingDiagnostic(
                            ref lastRecordingDiagnostic,
                            $"WaitNewFrame timeout frameCountSupported={frameCountSupported} sender={connectedSenderName}");
                    }

                    continue;
                }

                if (!receiver.ReceiveTexture())
                {
                    if (recordingMode)
                    {
                        EmitRecordingDiagnostic(
                            ref lastRecordingDiagnostic,
                            $"ReceiveTexture=false gpuRecordingMode={gpuRecordingMode} sharedReady={useSharedTextureCapture}");
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                if (receiver.IsUpdated)
                {
                    width = receiver.SenderWidth;
                    height = receiver.SenderHeight;
                    senderName = receiver.SenderName;
                    lastAcceptedSenderFrame = -1;
                    gpuCaptureFallbackWarned = false;
                    _nextPreviewCaptureTicks = 0;

                    RaiseStatus(new CaptureStatus(
                        true,
                        senderName,
                        width,
                        height,
                        receiver.SenderFps,
                        $"sender の状態が更新されました: {width} x {height}"));

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                if (!useSharedTextureCapture && !receiver.IsFrameNew)
                {
                    if (recordingMode)
                    {
                        EmitRecordingDiagnostic(
                            ref lastRecordingDiagnostic,
                            $"IsFrameNew=false gpuRecordingMode={gpuRecordingMode} sharedReady={useSharedTextureCapture} senderFrame={receiver.SenderFrame}");
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                var senderFrame = useSharedTextureCapture && frameCountSupported
                    ? frameCounter.SenderFrame
                    : receiver.SenderFrame;
                if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
                {
                    if (recordingMode)
                    {
                        EmitRecordingDiagnostic(
                            ref lastRecordingDiagnostic,
                            $"duplicate senderFrame={senderFrame} gpuRecordingMode={gpuRecordingMode} sharedReady={useSharedTextureCapture}");
                    }

                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                    continue;
                }

                lastAcceptedSenderFrame = senderFrame;
                var acceptedFrameTicks = Stopwatch.GetTimestamp();
                var recordingPathActive = recordingMode && frameHandler is not null;

                if (recordingMode)
                {
                    EmitRecordingDiagnostic(
                        ref lastRecordingDiagnostic,
                        $"accepted frame senderFrame={senderFrame} gpuRecordingMode={gpuRecordingMode} sharedReady={useSharedTextureCapture} recordingPathActive={recordingPathActive}");
                }

                if (!recordingPathActive)
                {
                    TryPublishPreviewFrame(
                        receiver,
                        sharedTextureReader,
                        useSharedTextureCapture,
                        connectedWidth,
                        connectedHeight,
                        connectedSenderName,
                        effectiveSenderFps,
                        acceptedFrameTicks);
                }

                if (frameHandler is not null)
                {
                    if (gpuRecordingMode && !useSharedTextureCapture)
                    {
                        DebugTrace.WriteLine(
                            "SpoutPollingService",
                            $"skip frame delivery because gpuRecordingMode=true and useSharedTextureCapture=false sender={connectedSenderName} size={connectedWidth}x{connectedHeight}");
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                        continue;
                    }

                    FramePacket? framePacket = null;
                    try
                    {
                        if (gpuRecordingMode && useSharedTextureCapture)
                        {
                            var gpuCaptureStarted = Stopwatch.GetTimestamp();
                            if (sharedTextureReader.TryCaptureFrame(out var gpuFrame, out var errorMessage, out var droppedForBackpressure))
                            {
                                DebugTrace.WriteTimingIfSlow(
                                    "SpoutPollingService",
                                    "TryCaptureFrame",
                                    gpuCaptureStarted,
                                    3.0,
                                    $"sender={connectedSenderName} size={connectedWidth}x{connectedHeight}");
                                DebugTrace.WriteLine(
                                    "SpoutPollingService",
                                    $"deliver gpu frame sender={connectedSenderName} size={connectedWidth}x{connectedHeight}");
                                framePacket = new FramePacket(
                                    null,
                                    gpuFrame,
                                    null,
                                    connectedWidth,
                                    connectedHeight,
                                    connectedSenderName,
                                    effectiveSenderFps,
                                    acceptedFrameTicks,
                                    DateTimeOffset.UtcNow);
                            }
                            else
                            {
                                if (droppedForBackpressure)
                                {
                                    EmitRecordingDiagnostic(
                                        ref lastRecordingDiagnostic,
                                        $"drop gpu frame due to capture backpressure sender={connectedSenderName}");
                                    WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
                                    continue;
                                }

                                Interlocked.Exchange(ref _gpuRecordingModeEnabled, 0);
                                gpuRecordingMode = false;
                                if (!gpuCaptureFallbackWarned)
                                {
                                    gpuCaptureFallbackWarned = true;
                                    RaiseStatus(new CaptureStatus(
                                        true,
                                        connectedSenderName,
                                        connectedWidth,
                                        connectedHeight,
                                        effectiveSenderFps,
                                        $"GPU 録画経路の初期化に失敗したため CPU 受信へフォールバックします: {errorMessage ?? "unknown error"}"));
                                }
                            }
                        }

                        if (framePacket is null)
                        {
                            DebugTrace.WriteLine(
                                "SpoutPollingService",
                                $"deliver cpu frame sender={connectedSenderName} size={connectedWidth}x{connectedHeight} gpuRecordingMode={gpuRecordingMode}");
                            var requiredLength = checked((int)(connectedWidth * connectedHeight * 4));
                            var frameCopy = PixelBufferLease.Rent(requiredLength);
                            var cpuCaptureStarted = Stopwatch.GetTimestamp();
                            CaptureCpuFrame(
                                receiver,
                                sharedTextureReader,
                                useSharedTextureCapture,
                                connectedSenderName,
                                connectedWidth,
                                connectedHeight,
                                frameCopy);
                            DebugTrace.WriteTimingIfSlow(
                                "SpoutPollingService",
                                "CaptureCpuFrame",
                                cpuCaptureStarted,
                                4.0,
                                $"sender={connectedSenderName} size={connectedWidth}x{connectedHeight} sharedPreferred={useSharedTextureCapture}");
                            framePacket = new FramePacket(
                                frameCopy,
                                null,
                                null,
                                connectedWidth,
                                connectedHeight,
                                connectedSenderName,
                                effectiveSenderFps,
                                acceptedFrameTicks,
                                DateTimeOffset.UtcNow);
                        }

                        var deliveryStarted = Stopwatch.GetTimestamp();
                        frameHandler.Invoke(this, framePacket);
                        DebugTrace.WriteTimingIfSlow(
                            "SpoutPollingService",
                            "FrameArrived handler",
                            deliveryStarted,
                            4.0,
                            $"sender={connectedSenderName} gpu={framePacket.GpuTexture is not null} cpu={framePacket.PixelBuffer is not null}");
                        framePacket = null;
                    }
                    finally
                    {
                        framePacket?.Dispose();
                    }
                }

                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            RaiseStatus(new CaptureStatus(
                wasConnected,
                senderName,
                width,
                height,
                receiver.SenderFps,
                $"受信ループでエラーが発生しました: {ex.Message}"));
        }
        finally
        {
            receiver.ReleaseReceiver();
            receiver.CloseOpenGL();
        }
    }

    private void TryPublishPreviewFrame(
        SpoutReceiver receiver,
        D3D11SpoutSharedTextureReader sharedTextureReader,
        bool preferSharedTextureCapture,
        uint width,
        uint height,
        string senderName,
        double senderFps,
        long stopwatchTicks)
    {
        if (_previewIntervalTicks > 0 && stopwatchTicks < Volatile.Read(ref _nextPreviewCaptureTicks))
        {
            return;
        }

        var requiredLength = checked((int)(width * height * 4));
        EnsurePreviewBufferSize(requiredLength);

        CaptureCpuFrame(receiver, sharedTextureReader, preferSharedTextureCapture, senderName, width, height, _previewBackBuffer, requiredLength);
        PublishPreviewFrame(width, height, senderName, senderFps, stopwatchTicks);
        Volatile.Write(ref _nextPreviewCaptureTicks, stopwatchTicks + _previewIntervalTicks);
    }

    private void CaptureCpuFrame(
        SpoutReceiver receiver,
        D3D11SpoutSharedTextureReader sharedTextureReader,
        bool preferSharedTextureCapture,
        string senderName,
        uint width,
        uint height,
        PixelBufferLease destination)
    {
        unsafe
        {
            fixed (byte* destinationPtr = destination.Buffer)
            {
                CaptureCpuFrame(receiver, sharedTextureReader, preferSharedTextureCapture, senderName, width, height, (IntPtr)destinationPtr, destination.Length);
            }
        }
    }

    private void CaptureCpuFrame(
        SpoutReceiver receiver,
        D3D11SpoutSharedTextureReader sharedTextureReader,
        bool preferSharedTextureCapture,
        string senderName,
        uint width,
        uint height,
        IntPtr destinationBuffer,
        int destinationLength)
    {
        if (preferSharedTextureCapture)
        {
            if (!sharedTextureReader.TryReadFrame(destinationBuffer, destinationLength, out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage ?? "shared texture の readback に失敗しました。");
            }

            return;
        }

        if (!TryReceivePreviewImage(receiver, senderName, width, height, destinationBuffer))
        {
            throw new InvalidOperationException("Spout 画像の受信に失敗しました。");
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

    private static unsafe bool TryReceivePreviewImage(
        SpoutReceiver receiver,
        string senderName,
        uint width,
        uint height,
        IntPtr destinationBuffer)
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
                (byte*)destinationBuffer,
                0x80E1u,
                false,
                0);
        }
    }

    private static bool TryPrepareSharedTextureReadback(
        SpoutReceiver receiver,
        D3D11SpoutSharedTextureReader sharedTextureReader,
        string senderName,
        out double effectiveSenderFps)
    {
        effectiveSenderFps = receiver.SenderFps;
        var senderGldx = receiver.SenderGLDX;
        var senderCpu = receiver.SenderCPU;
        var senderHandle = receiver.SenderHandle;
        var senderWidth = receiver.SenderWidth;
        var senderHeight = receiver.SenderHeight;
        var senderFormat = (Format)receiver.SenderFormat;
        var senderAdapter = receiver.Adapter;

        // Some senders expose both CPU-sharing metadata and a valid GL/DX shared handle.
        // In practice the handle is the strongest signal that we can stay on the GPU path.
        if (senderHandle == IntPtr.Zero)
        {
            DebugTrace.WriteLine(
                "SpoutPollingService",
                $"shared texture unavailable sender={senderName} SenderGLDX={senderGldx} SenderCPU={senderCpu} Handle=0x{senderHandle.ToInt64():X} Adapter={senderAdapter}");
            return false;
        }

        string? synchronizeError = null;
        var synchronized =
            sharedTextureReader.Matches(
                senderName,
                senderHandle,
                senderWidth,
                senderHeight,
                senderFormat) ||
            sharedTextureReader.TrySynchronizeSender(
                senderName,
                senderHandle,
                senderWidth,
                senderHeight,
                senderFormat,
                senderAdapter,
                out synchronizeError);
        if (!synchronized)
        {
            DebugTrace.WriteLine(
                "SpoutPollingService",
                $"shared texture not ready sender={senderName} SenderGLDX={senderGldx} SenderCPU={senderCpu} handle=0x{senderHandle.ToInt64():X} adapter={senderAdapter} format={senderFormat} error={synchronizeError ?? "unknown"}");
            return false;
        }

        return true;
    }

    private static bool TryAwaitSharedFrame(
        SpoutFrameCount frameCounter,
        string senderName,
        ref string frameCountSenderName,
        ref bool frameCountSupportProbed,
        ref bool frameCountSupported,
        double senderFps)
    {
        if (!string.Equals(frameCountSenderName, senderName, StringComparison.Ordinal))
        {
            frameCounter.CleanupFrameCount();
            frameCounter.EnableFrameCount(senderName);
            frameCountSenderName = senderName;
            frameCountSupportProbed = false;
            frameCountSupported = false;
        }

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

    private static bool WaitForNextFrame(SpoutFrameCount frameCounter, double senderFps)
    {
        var timeoutMilliseconds = senderFps > 1.0
            ? (uint)Math.Clamp((int)Math.Ceiling((1000.0 / Math.Min(senderFps, 120.0)) * 2.0), 4, 67)
            : 20u;
        return frameCounter.WaitNewFrame(timeoutMilliseconds);
    }

    private static bool IsSharedTextureCapable(SpoutReceiver receiver)
    {
        return receiver.SenderHandle != IntPtr.Zero;
    }

    private static void ApplyReceiveConfiguration(SpoutReceiver receiver, ReceiveConfiguration configuration)
    {
        switch (configuration)
        {
            case ReceiveConfiguration.SharedTexturePreferred:
                receiver.SetCPUmode(false);
                receiver.BufferMode = true;
                break;
            default:
                receiver.SetCPUmode(true);
                receiver.BufferMode = false;
                break;
        }
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

    private static long ResolvePreviewIntervalTicks()
    {
        const double defaultPreviewFps = 15.0;
        var overrideValue = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_PREVIEW_FPS");
        var previewFps = defaultPreviewFps;
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            double.TryParse(overrideValue, out var parsed) &&
            parsed > 0.0)
        {
            previewFps = Math.Clamp(parsed, 1.0, 60.0);
        }

        return (long)Math.Round(Stopwatch.Frequency / previewFps);
    }

    private static void EmitRecordingDiagnostic(ref string lastDiagnostic, string message)
    {
        if (string.Equals(lastDiagnostic, message, StringComparison.Ordinal))
        {
            return;
        }

        lastDiagnostic = message;
        DebugTrace.WriteLine("SpoutPollingService", message);
    }

    private enum ReceiveConfiguration
    {
        ImageFallback,
        SharedTexturePreferred
    }
}
