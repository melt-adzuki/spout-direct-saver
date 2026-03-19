using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Buffers.Binary;
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
    private readonly Channel<FrameWriteRequest> _writeChannel;
    private readonly Task _writerTask;

    private RecordedFrame? _currentFrame;
    private byte[]? _lastUniqueFrame;
    private ulong _lastUniqueFingerprint;
    private uint _recordedWidth;
    private uint _recordedHeight;
    private double _nominalSourceFps;
    private int _frameCounter;
    private bool _isCompleted;
    private bool _disposed;

    public RecordingSession(EncoderOption encoderOption, string outputPath)
    {
        _encoderOption = encoderOption;
        _outputPath = outputPath;
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "SpoutDirectSaver",
            Guid.NewGuid().ToString("N"));
        _spoolPath = Path.Combine(_temporaryDirectory, "frames.rgba");

        Directory.CreateDirectory(_temporaryDirectory);

        _writeChannel = Channel.CreateBounded<FrameWriteRequest>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writerTask = Task.Run(WriteFramesAsync);
    }

    public void AppendFrame(FramePacket frame)
    {
        lock (_gate)
        {
            ThrowIfCompleted();

            if (_currentFrame is null)
            {
                _recordedWidth = frame.Width;
                _recordedHeight = frame.Height;
                _nominalSourceFps = frame.SenderFps;
                RegisterFrame(frame, ComputeFingerprint(frame.PixelData));
                return;
            }

            if (frame.Width != _recordedWidth || frame.Height != _recordedHeight)
            {
                throw new InvalidOperationException("録画中に解像度が変わりました。現在の録画は固定解像度のみ対応しています。");
            }

            var fingerprint = ComputeFingerprint(frame.PixelData);
            if (_lastUniqueFrame is not null &&
                fingerprint == _lastUniqueFingerprint &&
                _lastUniqueFrame.AsSpan().SequenceEqual(frame.PixelData))
            {
                return;
            }

            FinalizeCurrentFrame(frame.StopwatchTicks);
            RegisterFrame(frame, fingerprint);
        }
    }

    public async Task<string> FinalizeAsync(VideoExportService exportService, CancellationToken cancellationToken)
    {
        RecordedFrame[] framesToEncode;

        lock (_gate)
        {
            ThrowIfCompleted();
            _isCompleted = true;

            if (_currentFrame is null)
            {
                throw new InvalidOperationException("録画中にフレームを受信できませんでした。");
            }

            FinalizeCurrentFrame(Stopwatch.GetTimestamp());
            _writeChannel.Writer.TryComplete();
            framesToEncode = _frames.ToArray();
        }

        try
        {
            await _writerTask.ConfigureAwait(false);

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
                _writeChannel.Writer.TryComplete();
            }
        }

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time writer failures.
        }
        finally
        {
            TryDeleteTemporaryDirectory();
        }
    }

    private void RegisterFrame(FramePacket frame, ulong fingerprint)
    {
        _frameCounter++;
        var recordedFrame = new RecordedFrame
        {
            FrameIndex = _frameCounter,
            StopwatchTicks = frame.StopwatchTicks,
            TimestampUtc = frame.TimestampUtc
        };

        _frames.Add(recordedFrame);
        _currentFrame = recordedFrame;
        _lastUniqueFrame = frame.PixelData;
        _lastUniqueFingerprint = fingerprint;

        var request = new FrameWriteRequest(frame.PixelData);
        if (!_writeChannel.Writer.TryWrite(request))
        {
            _writeChannel.Writer.WriteAsync(request).AsTask().GetAwaiter().GetResult();
        }
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

    private async Task WriteFramesAsync()
    {
        await using var fileStream = new FileStream(
            _spoolPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);

        await foreach (var request in _writeChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await fileStream.WriteAsync(request.PixelData).ConfigureAwait(false);
        }

        await fileStream.FlushAsync().ConfigureAwait(false);
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

    private void TryDeleteTemporaryDirectory()
    {
        if (!Directory.Exists(_temporaryDirectory))
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

    private sealed record FrameWriteRequest(byte[] PixelData);
}
