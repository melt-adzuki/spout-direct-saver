using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class RecordingSession : IAsyncDisposable
{
    private const double MinimumFrameDurationSeconds = 1.0 / 120.0;

    private readonly object _gate = new();
    private readonly EncoderOption _encoderOption;
    private readonly string _outputPath;
    private readonly string _temporaryDirectory;
    private readonly List<RecordedFrame> _frames = [];
    private readonly Channel<FrameWriteRequest> _writeChannel;
    private readonly Task _writerTask;

    private RecordedFrame? _currentFrame;
    private byte[]? _lastUniqueFrame;
    private uint _recordedWidth;
    private uint _recordedHeight;
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

        Directory.CreateDirectory(_temporaryDirectory);

        _writeChannel = Channel.CreateBounded<FrameWriteRequest>(new BoundedChannelOptions(2)
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
                RegisterFrame(frame);
                return;
            }

            if (frame.Width != _recordedWidth || frame.Height != _recordedHeight)
            {
                throw new InvalidOperationException("録画中に解像度が変わりました。現在の録画は固定解像度のみ対応しています。");
            }

            if (_lastUniqueFrame is not null &&
                _lastUniqueFrame.AsSpan().SequenceEqual(frame.PixelData))
            {
                return;
            }

            FinalizeCurrentFrame(frame.StopwatchTicks);
            RegisterFrame(frame);
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

            var manifestPath = await CreateConcatManifestAsync(framesToEncode, cancellationToken).ConfigureAwait(false);
            await exportService.ExportAsync(_encoderOption, manifestPath, _outputPath, cancellationToken).ConfigureAwait(false);
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

    private void RegisterFrame(FramePacket frame)
    {
        _frameCounter++;
        var fileName = $"frame_{_frameCounter:D6}.tga";
        var absolutePath = Path.Combine(_temporaryDirectory, fileName);

        var recordedFrame = new RecordedFrame
        {
            FileName = fileName,
            AbsolutePath = absolutePath,
            StopwatchTicks = frame.StopwatchTicks,
            TimestampUtc = frame.TimestampUtc
        };

        _frames.Add(recordedFrame);
        _currentFrame = recordedFrame;
        _lastUniqueFrame = frame.PixelData;

        var request = new FrameWriteRequest(absolutePath, frame.PixelData, frame.Width, frame.Height);
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
        await foreach (var request in _writeChannel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            SaveRgbaFrameAsTga(request);
        }
    }

    private static void SaveRgbaFrameAsTga(FrameWriteRequest request)
    {
        var pixelCount = checked((int)(request.Width * request.Height * 4));
        var rented = ArrayPool<byte>.Shared.Rent(pixelCount);

        try
        {
            PixelConversion.ConvertRgbaToBgra(request.PixelData, rented);
            using var fileStream = new FileStream(request.AbsolutePath, FileMode.Create, FileAccess.Write, FileShare.None, 131072);
            Span<byte> header = stackalloc byte[18];
            header[2] = 2;
            header[12] = (byte)(request.Width & 0xFF);
            header[13] = (byte)((request.Width >> 8) & 0xFF);
            header[14] = (byte)(request.Height & 0xFF);
            header[15] = (byte)((request.Height >> 8) & 0xFF);
            header[16] = 32;
            header[17] = 0x28;

            fileStream.Write(header);
            fileStream.Write(rented, 0, pixelCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task<string> CreateConcatManifestAsync(IReadOnlyList<RecordedFrame> frames, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_temporaryDirectory, "frames.ffconcat");

        await using var writer = new StreamWriter(manifestPath, false);
        await writer.WriteLineAsync("ffconcat version 1.0").ConfigureAwait(false);

        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"file '{frame.FileName}'").ConfigureAwait(false);
            await writer.WriteLineAsync($"duration {frame.DurationSeconds:0.000000}").ConfigureAwait(false);
        }

        var lastFrame = frames.Last();
        await writer.WriteLineAsync($"file '{lastFrame.FileName}'").ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        return manifestPath;
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

    private sealed record FrameWriteRequest(
        string AbsolutePath,
        byte[] PixelData,
        uint Width,
        uint Height);
}
