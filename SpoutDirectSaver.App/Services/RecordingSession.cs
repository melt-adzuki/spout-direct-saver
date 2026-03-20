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
    private readonly Task[] _compressionTasks;
    private readonly Task _writerTask;
    private readonly bool _blockOnBackpressure;
    private readonly FileOptions _spoolFileOptions;
    private readonly bool _writeSynchronously;
    private readonly FileStream? _syncSpoolStream;
    private readonly int _compressionWorkerCount;
    private readonly bool _useRealtimeEncoding;

    private RecordedFrame? _currentFrame;
    private PixelBufferLease? _lastUniqueFrame;
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
        _spoolFileOptions = writeThroughSpool
            ? FileOptions.Asynchronous | FileOptions.WriteThrough
            : FileOptions.Asynchronous;

        if (_useRealtimeEncoding)
        {
            _temporaryDirectory = string.Empty;
            _spoolPath = string.Empty;
            _compressionChannel = Channel.CreateBounded<FrameWriteRequest>(1);
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

        Directory.CreateDirectory(_temporaryDirectory);

        _compressionChannel = Channel.CreateBounded<FrameWriteRequest>(new BoundedChannelOptions(Math.Max(channelCapacity, 1))
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

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

                if (_currentFrame is null)
                {
                    _recordedWidth = frame.Width;
                    _recordedHeight = frame.Height;
                    _nominalSourceFps = frame.SenderFps;
                    TryRegisterFrame(frame, ComputeFingerprint(frame.PixelBuffer.Span));
                    return;
                }

                if (frame.Width != _recordedWidth || frame.Height != _recordedHeight)
                {
                    throw new InvalidOperationException("録画中に解像度が変わりました。現在の録画は固定解像度のみ対応しています。");
                }

                var fingerprint = ComputeFingerprint(frame.PixelBuffer.Span);
                if (fingerprint == _lastUniqueFingerprint)
                {
                    frame.PixelBuffer.Dispose();
                    return;
                }

                TryRegisterFrame(frame, fingerprint);
            }
        }
        catch
        {
            frame.PixelBuffer.Dispose();
            throw;
        }
    }

    public async Task<string> FinalizeAsync(VideoExportService exportService, CancellationToken cancellationToken)
    {
        RecordedFrame[] framesToEncode = [];
        RealtimeHybridWriter? realtimeWriter = null;

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
            _syncSpoolStream?.Dispose();
            TryDeleteTemporaryDirectory();
        }
    }

    private void TryRegisterFrame(FramePacket frame, ulong fingerprint)
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
        else
        {
            _compressSpoolFrames ??= true;
            recordedFrame.IsCompressed = _compressSpoolFrames.Value;

            var request = new FrameWriteRequest(recordedFrame.FrameIndex, recordedFrame, frame.PixelBuffer);
            if (_writeSynchronously)
            {
                WriteFrameSynchronously(request);
            }
            else if (!TryQueueFrameWrite(request))
            {
                frame.PixelBuffer.Dispose();
                return;
            }
        }

        _frameCounter++;
        _frames.Add(recordedFrame);
        _currentFrame = recordedFrame;
        _lastUniqueFrame = _useRealtimeEncoding ? frame.PixelBuffer : null;
        _lastUniqueFingerprint = fingerprint;
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
                byte[] payload;
                if (request.Frame.IsCompressed)
                {
                    payload = LZ4Pickler.Pickle(request.PixelData.Span, LZ4Level.L00_FAST);
                }
                else
                {
                    payload = GC.AllocateUninitializedArray<byte>(request.PixelData.Length);
                    request.PixelData.Span.CopyTo(payload);
                }

                _preparedFrames[request.Sequence] = new PreparedFrameWriteRequest(
                    request.Sequence,
                    request.Frame,
                    payload,
                    request.Frame.IsCompressed ? payload.Length : request.PixelData.Length);
                request.PixelData.Dispose();
                _preparedFrameSignal.Release();
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
                await WritePreparedFrameAsync(fileStream, prepared).ConfigureAwait(false);
                nextSequence++;
                continue;
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
        if (request.Frame.IsCompressed)
        {
            var compressedFrame = LZ4Pickler.Pickle(request.PixelData.Span, LZ4Level.L00_FAST);
            _syncSpoolStream.Write(compressedFrame);
        }
        else
        {
            _syncSpoolStream.Write(request.PixelData.Buffer, 0, request.PixelData.Length);
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

    private sealed record FrameWriteRequest(int Sequence, RecordedFrame Frame, PixelBufferLease PixelData);

    private sealed record PreparedFrameWriteRequest(int Sequence, RecordedFrame Frame, byte[] Payload, int Length);
}
