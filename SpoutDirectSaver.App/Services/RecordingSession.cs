using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Buffers.Binary;
using K4os.Compression.LZ4;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class RecordingSession : IAsyncDisposable
{
    private const double MinimumFrameDurationSeconds = 1.0 / 120.0;
    private readonly object _gate = new();
    private readonly EncoderOption _encoderOption;
    private readonly string _outputPath;
    private readonly string _temporaryDirectory;
    private readonly string _spoolPath;
    private readonly List<RecordedFrame> _frames = [];
    private readonly Channel<FrameWriteRequest> _compressionChannel;
    private readonly ConcurrentDictionary<int, PreparedFrameWriteRequest> _preparedFrames = new();
    private readonly SemaphoreSlim _preparedFrameSignal = new(0);
    private readonly SemaphoreSlim? _preparedFrameSlots;
    private readonly Task[] _compressionTasks;
    private readonly Task _writerTask;
    private readonly bool _blockOnBackpressure;
    private readonly FileOptions _spoolFileOptions;
    private readonly bool _writeSynchronously;
    private readonly FileStream? _syncSpoolStream;
    private readonly int _compressionWorkerCount;
    private readonly bool _useRealtimeEncoding;
    private readonly bool _useRealtimeRgbIntermediate;
    private readonly bool _disableHybridRgbIntermediate;
    private readonly bool _disableHybridAlphaSpool;
    private readonly LZ4Level _spoolCompressionLevel;

    private RecordedFrame? _currentFrame;
    private PixelBufferLease? _lastUniqueFrame;
    private GpuTextureFrame? _lastGpuFrame;
    private PixelBufferLease? _lastAlphaFrame;
    private ulong _lastUniqueFingerprint;
    private uint _recordedWidth;
    private uint _recordedHeight;
    private double _nominalSourceFps;
    private double _outputFrameRate;
    private double _accumulatedTimelineFrames;
    private int _emittedTimelineFrames;
    private int _frameCounter;
    private bool _isCompleted;
    private bool _disposed;
    private bool? _compressSpoolFrames;
    private int _remainingCompressionWorkers;
    private RealtimeHybridWriter? _realtimeWriter;
    private RealtimeRgbNvencWriter? _rgbIntermediateWriter;
    private readonly string? _rgbIntermediatePath;
    private readonly string? _alphaTrackPath;

    public RecordingSession(
        EncoderOption encoderOption,
        string outputPath,
        int channelCapacity = 32,
        bool blockOnBackpressure = true,
        bool? compressSpoolFrames = null,
        bool writeThroughSpool = false,
        bool writeSynchronously = false)
    {
        _encoderOption = encoderOption;
        _outputPath = outputPath;
        _blockOnBackpressure = blockOnBackpressure;
        _compressSpoolFrames = compressSpoolFrames;
        _writeSynchronously = writeSynchronously;
        _useRealtimeEncoding = encoderOption.RequiresRealtimeEncoding;
        _useRealtimeRgbIntermediate =
            !_useRealtimeEncoding &&
            encoderOption.Kind == EncoderProfileKind.HevcNvencFfv1AlphaMkv;
        _disableHybridRgbIntermediate = IsEnabled("SPOUT_DIRECT_SAVER_DISABLE_HYBRID_RGB");
        _disableHybridAlphaSpool = IsEnabled("SPOUT_DIRECT_SAVER_DISABLE_HYBRID_ALPHA");
        _spoolCompressionLevel = ResolveSpoolCompressionLevel(encoderOption);
        _spoolFileOptions = writeThroughSpool
            ? FileOptions.Asynchronous | FileOptions.WriteThrough
            : FileOptions.Asynchronous;

        if (_useRealtimeEncoding)
        {
            _temporaryDirectory = string.Empty;
            _spoolPath = string.Empty;
            _compressionChannel = Channel.CreateBounded<FrameWriteRequest>(1);
            _preparedFrameSlots = null;
            _compressionWorkerCount = 0;
            _remainingCompressionWorkers = 0;
            _compressionTasks = Array.Empty<Task>();
            _syncSpoolStream = null;
            _writerTask = Task.CompletedTask;
            return;
        }

        _temporaryDirectory = Path.Combine(
            ResolveCacheRoot(outputPath),
            Guid.NewGuid().ToString("N"));
        _spoolPath = Path.Combine(_temporaryDirectory, "frames.bin");
        _rgbIntermediatePath = _useRealtimeRgbIntermediate
            ? Path.Combine(_temporaryDirectory, "rgb.mp4")
            : null;
        _alphaTrackPath = _useRealtimeRgbIntermediate
            ? Path.Combine(_temporaryDirectory, "alpha.mkv")
            : null;

        Directory.CreateDirectory(_temporaryDirectory);

        _compressionChannel = Channel.CreateBounded<FrameWriteRequest>(new BoundedChannelOptions(Math.Max(channelCapacity, 1))
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        var preparedFrameCapacity = Math.Max(channelCapacity, 1);
        _preparedFrameSlots = new SemaphoreSlim(preparedFrameCapacity, preparedFrameCapacity);

        if (_writeSynchronously)
        {
            _compressionWorkerCount = 0;
            _remainingCompressionWorkers = 0;
            _compressionTasks = Array.Empty<Task>();
            _syncSpoolStream = CreateSpoolStream();
            _writerTask = Task.CompletedTask;
        }
        else
        {
            _compressionWorkerCount = DetermineCompressionWorkerCount();
            _remainingCompressionWorkers = _compressionWorkerCount;
            _compressionTasks = new Task[_compressionWorkerCount];

            for (var index = 0; index < _compressionWorkerCount; index++)
            {
                _compressionTasks[index] = Task.Factory.StartNew(
                    static state =>
                    {
                        var session = (RecordingSession)state!;
                        using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                        session.CompressFramesAsync().GetAwaiter().GetResult();
                    },
                    this,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }

            _writerTask = Task.Factory.StartNew(
                static state =>
                {
                    var session = (RecordingSession)state!;
                    using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                    session.WritePreparedFramesAsync().GetAwaiter().GetResult();
                },
                this,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void AppendFrame(FramePacket frame)
    {
        try
        {
            lock (_gate)
            {
                ThrowIfCompleted();

                if (frame.GpuTexture is not null)
                {
                    AppendGpuFrame(frame);
                    return;
                }

                if (frame.PixelBuffer is null)
                {
                    throw new InvalidOperationException("CPU frame payload が存在しません。");
                }

                if (_currentFrame is null)
                {
                    _recordedWidth = frame.Width;
                    _recordedHeight = frame.Height;
                    _nominalSourceFps = frame.SenderFps;
                    TryRegisterCpuFrame(frame, ComputeFingerprint(frame.PixelBuffer.Span));
                    return;
                }

                if (frame.Width != _recordedWidth || frame.Height != _recordedHeight)
                {
                    throw new InvalidOperationException("録画中に解像度が変わりました。現在の録画は固定解像度のみ対応しています。");
                }

                var fingerprint = ComputeFingerprint(frame.PixelBuffer.Span);
                if (fingerprint == _lastUniqueFingerprint &&
                    _lastUniqueFrame is not null &&
                    _lastUniqueFrame.Length == frame.PixelBuffer.Length &&
                    _lastUniqueFrame.Span.SequenceEqual(frame.PixelBuffer.Span))
                {
                    frame.Dispose();
                    return;
                }

                TryRegisterCpuFrame(frame, fingerprint);
            }
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public async Task<string> FinalizeAsync(VideoExportService exportService, CancellationToken cancellationToken)
    {
        RecordedFrame[] framesToEncode = [];
        RealtimeHybridWriter? realtimeWriter = null;
        RealtimeRgbNvencWriter? rgbIntermediateWriter = null;

        lock (_gate)
        {
            ThrowIfCompleted();
            _isCompleted = true;

            if (_currentFrame is null)
            {
                throw new InvalidOperationException("録画中にフレームを受信できませんでした。");
            }

            FinalizeCurrentFrame(Stopwatch.GetTimestamp());
            if (_useRealtimeEncoding)
            {
                if (_currentFrame is not null && _lastUniqueFrame is not null)
                {
                    QueueFrameForRealtimeEncode(_currentFrame, _lastUniqueFrame);
                }

                realtimeWriter = _realtimeWriter;
            }
            else if (_useRealtimeRgbIntermediate)
            {
                if (_currentFrame is not null && _lastGpuFrame is not null && _lastAlphaFrame is not null)
                {
                    QueueHybridFrame(_currentFrame, _lastGpuFrame, _lastAlphaFrame);
                    _lastGpuFrame = null;
                    _lastAlphaFrame = null;
                }
                else if (_currentFrame is not null && _lastUniqueFrame is not null)
                {
                    QueueHybridFrame(_currentFrame, _lastUniqueFrame);
                    _lastUniqueFrame = null;
                }

                _compressionChannel.Writer.TryComplete();
                framesToEncode = _frames.ToArray();
                rgbIntermediateWriter = _rgbIntermediateWriter;
            }
            else
            {
                _compressionChannel.Writer.TryComplete();
                framesToEncode = _frames.ToArray();
            }
        }

        try
        {
            if (_useRealtimeEncoding)
            {
                if (realtimeWriter is null)
                {
                    throw new InvalidOperationException("realtime エンコーダーが初期化されていません。");
                }

                return await realtimeWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_useRealtimeRgbIntermediate)
            {
                if (!_disableHybridRgbIntermediate &&
                    (rgbIntermediateWriter is null || _rgbIntermediatePath is null))
                {
                    throw new InvalidOperationException("RGB intermediate encoder が初期化されていません。");
                }

                if (!_disableHybridRgbIntermediate)
                {
                    await rgbIntermediateWriter!.CompleteAsync(cancellationToken).ConfigureAwait(false);
                }

                if (_compressionTasks.Length > 0)
                {
                    await Task.WhenAll(_compressionTasks).ConfigureAwait(false);
                }

                if (!_disableHybridAlphaSpool)
                {
                    await _writerTask.ConfigureAwait(false);
                    _syncSpoolStream?.Dispose();

                    if (_alphaTrackPath is null)
                    {
                        throw new InvalidOperationException("alpha track path が初期化されていません。");
                    }

                    await exportService.ExportAlphaTrackAsync(
                        _spoolPath,
                        framesToEncode,
                        _recordedWidth,
                        _recordedHeight,
                        GetOutputFrameRate(),
                        _alphaTrackPath,
                        cancellationToken).ConfigureAwait(false);
                }

                if (_disableHybridRgbIntermediate)
                {
                    await exportService.RemuxSingleVideoAsync(
                        _alphaTrackPath!,
                        _outputPath,
                        cancellationToken).ConfigureAwait(false);
                    return _outputPath;
                }

                await exportService.RemuxSingleVideoAsync(
                    _rgbIntermediatePath!,
                    _outputPath,
                    cancellationToken).ConfigureAwait(false);

                if (!_disableHybridAlphaSpool)
                {
                    var alphaSidecarPath = BuildAlphaSidecarPath(_outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(alphaSidecarPath)!);
                    File.Copy(_alphaTrackPath!, alphaSidecarPath, overwrite: true);
                }

                return _outputPath;
            }

            if (_compressionTasks.Length > 0)
            {
                await Task.WhenAll(_compressionTasks).ConfigureAwait(false);
            }
            await _writerTask.ConfigureAwait(false);
            _syncSpoolStream?.Dispose();

            await exportService.ExportAsync(
                _encoderOption,
                _spoolPath,
                framesToEncode,
                _recordedWidth,
                _recordedHeight,
                GetOutputFrameRate(),
                _outputPath,
                cancellationToken).ConfigureAwait(false);
            return _outputPath;
        }
        finally
        {
            _lastUniqueFrame?.Dispose();
            _lastUniqueFrame = null;
            _lastGpuFrame?.Dispose();
            _lastGpuFrame = null;
            _lastAlphaFrame?.Dispose();
            _lastAlphaFrame = null;
            _syncSpoolStream?.Dispose();
            TryDeleteTemporaryDirectory();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            if (!_isCompleted)
            {
                _isCompleted = true;
                if (!_useRealtimeEncoding)
                {
                    _compressionChannel.Writer.TryComplete();
                }
            }
        }

        try
        {
            if (_useRealtimeEncoding)
            {
                if (_realtimeWriter is not null)
                {
                    await _realtimeWriter.DisposeAsync().ConfigureAwait(false);
                }
            }
            else if (_useRealtimeRgbIntermediate)
            {
                _compressionChannel.Writer.TryComplete();

                if (_rgbIntermediateWriter is not null)
                {
                    await _rgbIntermediateWriter.DisposeAsync().ConfigureAwait(false);
                }

                if (_compressionTasks.Length > 0)
                {
                    await Task.WhenAll(_compressionTasks).ConfigureAwait(false);
                }
                if (!_disableHybridAlphaSpool)
                {
                    await _writerTask.ConfigureAwait(false);
                }
            }
            else
            {
                if (_compressionTasks.Length > 0)
                {
                    await Task.WhenAll(_compressionTasks).ConfigureAwait(false);
                }
                await _writerTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore cleanup-time writer failures.
        }
        finally
        {
            _lastUniqueFrame?.Dispose();
            _lastUniqueFrame = null;
            _lastGpuFrame?.Dispose();
            _lastGpuFrame = null;
            _lastAlphaFrame?.Dispose();
            _lastAlphaFrame = null;
            _syncSpoolStream?.Dispose();
            TryDeleteTemporaryDirectory();
        }
    }

    private void AppendGpuFrame(FramePacket frame)
    {
        if (!_useRealtimeRgbIntermediate)
        {
            throw new InvalidOperationException("GPU frame は HEVC hybrid 録画でのみ利用できます。");
        }

        if (frame.GpuTexture is null || frame.AlphaBuffer is null)
        {
            throw new InvalidOperationException("GPU frame payload が不足しています。");
        }

        if (_currentFrame is null)
        {
            _recordedWidth = frame.Width;
            _recordedHeight = frame.Height;
            _nominalSourceFps = frame.SenderFps;
            TryRegisterGpuFrame(frame);
            return;
        }

        if (frame.Width != _recordedWidth || frame.Height != _recordedHeight)
        {
            throw new InvalidOperationException("録画中に解像度が変わりました。現在の録画は固定解像度のみ対応しています。");
        }

        TryRegisterGpuFrame(frame);
    }

    private void TryRegisterCpuFrame(FramePacket frame, ulong fingerprint)
    {
        if (_currentFrame is not null)
        {
            FinalizeCurrentFrame(frame.StopwatchTicks);
            if (_useRealtimeEncoding)
            {
                if (_lastUniqueFrame is null)
                {
                    throw new InvalidOperationException("realtime エンコード用の前フレームが見つかりません。");
                }

                QueueFrameForRealtimeEncode(_currentFrame, _lastUniqueFrame);
            }
            else if (_useRealtimeRgbIntermediate)
            {
                if (_lastUniqueFrame is null)
                {
                    throw new InvalidOperationException("RGB intermediate 用の前フレームが見つかりません。");
                }

                QueueHybridFrame(_currentFrame, _lastUniqueFrame);
                _lastUniqueFrame = null;
            }
            else
            {
                _lastUniqueFrame?.Dispose();
                _lastUniqueFrame = null;
            }
        }

        var recordedFrame = new RecordedFrame
        {
            FrameIndex = _frameCounter + 1,
            StopwatchTicks = frame.StopwatchTicks,
            TimestampUtc = frame.TimestampUtc,
            IsCompressed = false
        };

        if (_useRealtimeEncoding)
        {
            EnsureRealtimeWriterStarted();
        }
        else if (_useRealtimeRgbIntermediate)
        {
            recordedFrame.IsCompressed = _compressSpoolFrames ?? true;
        }
        else
        {
            _compressSpoolFrames ??= true;
            recordedFrame.IsCompressed = _compressSpoolFrames.Value;

            var pixelBuffer = frame.PixelBuffer ?? throw new InvalidOperationException("CPU frame payload が存在しません。");
            pixelBuffer.Retain();
            var comparisonFrame = pixelBuffer;
            var request = new FrameWriteRequest(recordedFrame.FrameIndex, recordedFrame, pixelBuffer);
            try
            {
                if (_writeSynchronously)
                {
                    WriteFrameSynchronously(request);
                }
                else if (!TryQueueFrameWrite(request))
                {
                    comparisonFrame.Dispose();
                    frame.PixelBuffer.Dispose();
                    return;
                }
            }
            catch
            {
                comparisonFrame.Dispose();
                throw;
            }

            _lastUniqueFrame = comparisonFrame;
        }

        _frameCounter++;
        _frames.Add(recordedFrame);
        _currentFrame = recordedFrame;
        if (_useRealtimeEncoding || _useRealtimeRgbIntermediate)
        {
            _lastUniqueFrame = frame.PixelBuffer!;
        }
        _lastUniqueFingerprint = fingerprint;
    }

    private void TryRegisterGpuFrame(FramePacket frame)
    {
        if (_currentFrame is not null)
        {
            FinalizeCurrentFrame(frame.StopwatchTicks);
            if (_lastGpuFrame is null || _lastAlphaFrame is null)
            {
                throw new InvalidOperationException("GPU intermediate 用の前フレームが見つかりません。");
            }

            QueueHybridFrame(_currentFrame, _lastGpuFrame, _lastAlphaFrame);
            _lastGpuFrame = null;
            _lastAlphaFrame = null;
        }

        _compressSpoolFrames ??= true;
        var recordedFrame = new RecordedFrame
        {
            FrameIndex = _frameCounter + 1,
            StopwatchTicks = frame.StopwatchTicks,
            TimestampUtc = frame.TimestampUtc,
            IsCompressed = _compressSpoolFrames.Value
        };

        _frameCounter++;
        _frames.Add(recordedFrame);
        _currentFrame = recordedFrame;
        _lastGpuFrame = frame.GpuTexture!;
        _lastAlphaFrame = frame.AlphaBuffer!;
    }

    private void FinalizeCurrentFrame(long completedStopwatchTicks)
    {
        if (_currentFrame is null)
        {
            return;
        }

        var duration = (completedStopwatchTicks - _currentFrame.StopwatchTicks) / (double)Stopwatch.Frequency;
        _currentFrame.DurationSeconds = Math.Max(duration, MinimumFrameDurationSeconds);
    }

    private async Task CompressFramesAsync()
    {
        try
        {
            await foreach (var request in _compressionChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (_preparedFrameSlots is null)
                {
                    throw new InvalidOperationException("prepared frame slot limiter が初期化されていません。");
                }

                await _preparedFrameSlots.WaitAsync().ConfigureAwait(false);
                var preparedFrameQueued = false;
                byte[] payload;
                try
                {
                    var payloadLength = request.PixelData.Length;
                    switch (request.PayloadKind)
                    {
                        case FramePayloadKind.AlphaFromBgra:
                        {
                            var alphaBuffer = ExtractAlphaPlane(request.PixelData.Span);
                            payload = request.Frame.IsCompressed
                                ? LZ4Pickler.Pickle(alphaBuffer, _spoolCompressionLevel)
                                : alphaBuffer;
                            payloadLength = request.Frame.IsCompressed ? payload.Length : alphaBuffer.Length;
                            break;
                        }
                        case FramePayloadKind.AlphaPlane:
                        {
                            if (request.Frame.IsCompressed)
                            {
                                payload = LZ4Pickler.Pickle(request.PixelData.Span, _spoolCompressionLevel);
                                payloadLength = payload.Length;
                            }
                            else
                            {
                                payload = GC.AllocateUninitializedArray<byte>(request.PixelData.Length);
                                request.PixelData.Span.CopyTo(payload);
                                payloadLength = request.PixelData.Length;
                            }

                            break;
                        }
                        default:
                        {
                            if (request.Frame.IsCompressed)
                            {
                                payload = LZ4Pickler.Pickle(request.PixelData.Span, _spoolCompressionLevel);
                                payloadLength = payload.Length;
                            }
                            else
                            {
                                payload = GC.AllocateUninitializedArray<byte>(request.PixelData.Length);
                                request.PixelData.Span.CopyTo(payload);
                                payloadLength = request.PixelData.Length;
                            }

                            break;
                        }
                    }

                    _preparedFrames[request.Sequence] = new PreparedFrameWriteRequest(
                        request.Sequence,
                        request.Frame,
                        payload,
                        payloadLength);
                    preparedFrameQueued = true;
                    _preparedFrameSignal.Release();
                }
                finally
                {
                    request.PixelData.Dispose();
                    if (!preparedFrameQueued)
                    {
                        _preparedFrameSlots.Release();
                    }
                }
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _remainingCompressionWorkers) == 0)
            {
                _preparedFrameSignal.Release();
            }
        }
    }

    private async Task WritePreparedFramesAsync()
    {
        await using var fileStream = CreateSpoolStream();
        var nextSequence = 1;

        while (true)
        {
            if (_preparedFrames.TryRemove(nextSequence, out var prepared))
            {
                try
                {
                    await WritePreparedFrameAsync(fileStream, prepared).ConfigureAwait(false);
                    nextSequence++;
                    continue;
                }
                finally
                {
                    _preparedFrameSlots?.Release();
                }
            }

            if (Volatile.Read(ref _remainingCompressionWorkers) == 0)
            {
                if (_preparedFrames.IsEmpty)
                {
                    break;
                }

                throw new InvalidOperationException("一時フレームスプールの順序が壊れています。");
            }

            await _preparedFrameSignal.WaitAsync().ConfigureAwait(false);
        }

        await fileStream.FlushAsync().ConfigureAwait(false);
    }

    private void WriteFrameSynchronously(FrameWriteRequest request)
    {
        ObjectDisposedException.ThrowIf(_syncSpoolStream is null, this);

        var frameOffset = _syncSpoolStream.Position;
        switch (request.PayloadKind)
        {
            case FramePayloadKind.AlphaFromBgra:
            {
                var alphaBuffer = ExtractAlphaPlane(request.PixelData.Span);
                if (request.Frame.IsCompressed)
                {
                    var compressedFrame = LZ4Pickler.Pickle(alphaBuffer, _spoolCompressionLevel);
                    _syncSpoolStream.Write(compressedFrame);
                }
                else
                {
                    _syncSpoolStream.Write(alphaBuffer, 0, alphaBuffer.Length);
                }

                break;
            }
            case FramePayloadKind.AlphaPlane:
            {
                if (request.Frame.IsCompressed)
                {
                    var compressedFrame = LZ4Pickler.Pickle(request.PixelData.Span, _spoolCompressionLevel);
                    _syncSpoolStream.Write(compressedFrame);
                }
                else
                {
                    _syncSpoolStream.Write(request.PixelData.Buffer, 0, request.PixelData.Length);
                }

                break;
            }
            default:
            {
                if (request.Frame.IsCompressed)
                {
                    var compressedFrame = LZ4Pickler.Pickle(request.PixelData.Span, _spoolCompressionLevel);
                    _syncSpoolStream.Write(compressedFrame);
                }
                else
                {
                    _syncSpoolStream.Write(request.PixelData.Buffer, 0, request.PixelData.Length);
                }

                break;
            }
        }

        request.Frame.SpoolOffset = frameOffset;
        request.Frame.SpoolLength = checked((int)(_syncSpoolStream.Position - frameOffset));
        _syncSpoolStream.Flush();
        request.PixelData.Dispose();
    }

    private async Task WritePreparedFrameAsync(FileStream fileStream, PreparedFrameWriteRequest preparedFrame)
    {
        var frameOffset = fileStream.Position;
        await fileStream.WriteAsync(preparedFrame.Payload.AsMemory(0, preparedFrame.Length)).ConfigureAwait(false);
        preparedFrame.Frame.SpoolOffset = frameOffset;
        preparedFrame.Frame.SpoolLength = checked((int)(fileStream.Position - frameOffset));
    }

    private FileStream CreateSpoolStream()
    {
        return new FileStream(
            _spoolPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            4 * 1024 * 1024,
            _spoolFileOptions);
    }

    private bool TryQueueFrameWrite(FrameWriteRequest request)
    {
        if (_blockOnBackpressure)
        {
            _compressionChannel.Writer.WriteAsync(request).AsTask().GetAwaiter().GetResult();
            return true;
        }

        return _compressionChannel.Writer.TryWrite(request);
    }

    private double GetOutputFrameRate()
    {
        if (_nominalSourceFps >= 100.0)
        {
            return 120.0;
        }

        if (_nominalSourceFps >= 50.0)
        {
            return 60.0;
        }

        if (_nominalSourceFps >= 25.0)
        {
            return 30.0;
        }

        return 120.0;
    }

    private void ThrowIfCompleted()
    {
        if (_isCompleted)
        {
            throw new InvalidOperationException("この録画セッションは既に終了しています。");
        }
    }

    private void EnsureRealtimeWriterStarted()
    {
        if (!_useRealtimeEncoding || _realtimeWriter is not null)
        {
            return;
        }

        _outputFrameRate = GetOutputFrameRate();
        _realtimeWriter = new RealtimeHybridWriter(
            _recordedWidth,
            _recordedHeight,
            _outputFrameRate,
            _outputPath,
            ResolveCacheRoot(_outputPath));
    }

    private void EnsureRgbIntermediateWriterStarted(GpuTextureFrame gpuFrame)
    {
        if (!_useRealtimeRgbIntermediate || _rgbIntermediateWriter is not null)
        {
            return;
        }

        if (_rgbIntermediatePath is null)
        {
            throw new InvalidOperationException("RGB intermediate path が初期化されていません。");
        }

        _outputFrameRate = GetOutputFrameRate();
        _rgbIntermediateWriter = new RealtimeRgbNvencWriter(
            _recordedWidth,
            _recordedHeight,
            _outputFrameRate,
            _rgbIntermediatePath,
            gpuFrame.Device);
    }

    private void EnsureRgbIntermediateWriterStarted()
    {
        if (!_useRealtimeRgbIntermediate || _rgbIntermediateWriter is not null)
        {
            return;
        }

        if (_rgbIntermediatePath is null)
        {
            throw new InvalidOperationException("RGB intermediate path が初期化されていません。");
        }

        _outputFrameRate = GetOutputFrameRate();
        _rgbIntermediateWriter = new RealtimeRgbNvencWriter(
            _recordedWidth,
            _recordedHeight,
            _outputFrameRate,
            _rgbIntermediatePath);
    }

    private void QueueFrameForRealtimeEncode(RecordedFrame frame, PixelBufferLease pixelData)
    {
        EnsureRealtimeWriterStarted();
        if (_realtimeWriter is null)
        {
            throw new InvalidOperationException("realtime エンコーダーが初期化されていません。");
        }

        _accumulatedTimelineFrames += frame.DurationSeconds * _outputFrameRate;
        var targetTotalFrames = Math.Max(_emittedTimelineFrames + 1, (int)Math.Round(_accumulatedTimelineFrames));
        var repeatCount = targetTotalFrames - _emittedTimelineFrames;

        _realtimeWriter.QueueFrame(pixelData, repeatCount);
        _emittedTimelineFrames = targetTotalFrames;
    }

    private void QueueFrameForRealtimeRgbIntermediate(RecordedFrame frame, GpuTextureFrame gpuFrame)
    {
        EnsureRgbIntermediateWriterStarted(gpuFrame);
        if (_rgbIntermediateWriter is null)
        {
            throw new InvalidOperationException("RGB intermediate writer が初期化されていません。");
        }

        _accumulatedTimelineFrames += frame.DurationSeconds * _outputFrameRate;
        var targetTotalFrames = Math.Max(_emittedTimelineFrames + 1, (int)Math.Round(_accumulatedTimelineFrames));
        var repeatCount = targetTotalFrames - _emittedTimelineFrames;

        _rgbIntermediateWriter.QueueFrame(gpuFrame, repeatCount);
        _emittedTimelineFrames = targetTotalFrames;
    }

    private void QueueFrameForRealtimeRgbIntermediate(RecordedFrame frame, PixelBufferLease pixelData)
    {
        EnsureRgbIntermediateWriterStarted();
        if (_rgbIntermediateWriter is null)
        {
            throw new InvalidOperationException("RGB intermediate writer が初期化されていません。");
        }

        _accumulatedTimelineFrames += frame.DurationSeconds * _outputFrameRate;
        var targetTotalFrames = Math.Max(_emittedTimelineFrames + 1, (int)Math.Round(_accumulatedTimelineFrames));
        var repeatCount = targetTotalFrames - _emittedTimelineFrames;

        _rgbIntermediateWriter.QueueFrame(pixelData, repeatCount);
        _emittedTimelineFrames = targetTotalFrames;
    }

    private void QueueHybridFrame(RecordedFrame frame, PixelBufferLease pixelData)
    {
        if (!_disableHybridAlphaSpool)
        {
            pixelData.Retain();
            var request = new FrameWriteRequest(frame.FrameIndex, frame, pixelData, FramePayloadKind.AlphaFromBgra);
            if (_writeSynchronously)
            {
                WriteFrameSynchronously(request);
            }
            else if (!TryQueueFrameWrite(request))
            {
                pixelData.Dispose();
                throw new InvalidOperationException("alpha スプールへのフレーム投入に失敗しました。");
            }
        }

        if (!_disableHybridRgbIntermediate)
        {
            QueueFrameForRealtimeRgbIntermediate(frame, pixelData);
            return;
        }

        pixelData.Dispose();
    }

    private void QueueHybridFrame(RecordedFrame frame, GpuTextureFrame gpuFrame, PixelBufferLease alphaData)
    {
        if (!_disableHybridAlphaSpool)
        {
            var request = new FrameWriteRequest(frame.FrameIndex, frame, alphaData, FramePayloadKind.AlphaPlane);
            if (_writeSynchronously)
            {
                WriteFrameSynchronously(request);
            }
            else if (!TryQueueFrameWrite(request))
            {
                alphaData.Dispose();
                gpuFrame.Dispose();
                throw new InvalidOperationException("alpha スプールへのフレーム投入に失敗しました。");
            }
        }
        else
        {
            alphaData.Dispose();
        }

        if (!_disableHybridRgbIntermediate)
        {
            QueueFrameForRealtimeRgbIntermediate(frame, gpuFrame);
            return;
        }

        gpuFrame.Dispose();
    }

    private void TryDeleteTemporaryDirectory()
    {
        if (string.IsNullOrWhiteSpace(_temporaryDirectory) || !Directory.Exists(_temporaryDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(_temporaryDirectory, true);
        }
        catch
        {
            // Keep temp files if cleanup fails.
        }
    }

    private static string ResolveCacheRoot(string outputPath)
    {
        var overrideRoot = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.Combine(overrideRoot, "SpoutDirectSaverCache");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, "SpoutDirectSaver", "Cache");
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return Path.Combine(desktop, "SpoutDirectSaverCache");
        }

        return Path.Combine(Path.GetTempPath(), "SpoutDirectSaver");
    }

    private static string BuildAlphaSidecarPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}.alpha.mkv");
    }

    private static ulong ComputeFingerprint(ReadOnlySpan<byte> pixelData)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        hash = Mix(hash, (ulong)pixelData.Length);
        if (pixelData.IsEmpty)
        {
            return hash;
        }

        AddWindow(pixelData, 0, Math.Min(128, pixelData.Length), ref hash);
        AddWindow(pixelData, Math.Max(0, (pixelData.Length / 2) - 64), Math.Min(128, pixelData.Length), ref hash);
        AddWindow(pixelData, Math.Max(0, pixelData.Length - 128), Math.Min(128, pixelData.Length), ref hash);

        var checkpoints = 8;
        for (var i = 1; i <= checkpoints; i++)
        {
            var offset = (int)(((long)pixelData.Length - 8) * i / (checkpoints + 1));
            offset = Math.Clamp(offset, 0, Math.Max(0, pixelData.Length - 8));
            hash = Mix(hash, BinaryPrimitives.ReadUInt64LittleEndian(pixelData.Slice(offset, 8)));
        }

        return hash;

        static void AddWindow(ReadOnlySpan<byte> data, int offset, int length, ref ulong targetHash)
        {
            var end = Math.Min(offset + length, data.Length);
            for (var index = offset; index < end; index++)
            {
                targetHash = Mix(targetHash, data[index]);
            }
        }

        static ulong Mix(ulong current, ulong value)
        {
            return (current ^ value) * prime;
        }
    }

    private static int DetermineCompressionWorkerCount()
    {
        return Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    }

    private static byte[] ExtractAlphaPlane(ReadOnlySpan<byte> bgraFrame)
    {
        var pixelCount = bgraFrame.Length / 4;
        var alphaBuffer = GC.AllocateUninitializedArray<byte>(pixelCount);
        var destination = alphaBuffer.AsSpan();
        var sourceIndex = 3;
        for (var targetIndex = 0; targetIndex < pixelCount; targetIndex++, sourceIndex += 4)
        {
            destination[targetIndex] = bgraFrame[sourceIndex];
        }

        return alphaBuffer;
    }

    private static LZ4Level ResolveSpoolCompressionLevel(EncoderOption encoderOption)
    {
        var overrideValue = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_SPOOL_COMPRESSION");
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            Enum.TryParse<LZ4Level>(overrideValue, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return LZ4Level.L00_FAST;
    }

    private static bool IsEnabled(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private enum FramePayloadKind
    {
        Bgra,
        AlphaFromBgra,
        AlphaPlane
    }

    private sealed record FrameWriteRequest(int Sequence, RecordedFrame Frame, PixelBufferLease PixelData, FramePayloadKind PayloadKind = FramePayloadKind.Bgra);

    private sealed record PreparedFrameWriteRequest(int Sequence, RecordedFrame Frame, byte[] Payload, int Length);
}
