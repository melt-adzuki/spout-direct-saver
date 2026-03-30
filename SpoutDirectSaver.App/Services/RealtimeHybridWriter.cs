using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeHybridWriter : IAsyncDisposable
{
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _writerTask;
    private readonly MediaFoundationHevcWriter _mainWriter;
    private readonly RealtimeGrayFfv1Writer _alphaWriter;
    private readonly string _temporaryDirectory;
    private readonly string _mainVideoPath;
    private readonly string _alphaVideoPath;
    private readonly string _finalOutputPath;
    private readonly int _pixelCount;
    private readonly bool _disableMainWriter;
    private readonly bool _disableAlphaWriter;
    private bool _completed;
    private bool _disposed;

    public RealtimeHybridWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        string cacheRoot,
        int queueCapacity = 8)
    {
        _finalOutputPath = outputPath;
        _pixelCount = checked((int)(width * height));
        _disableMainWriter = IsEnabled("SPOUT_DIRECT_SAVER_DISABLE_MAIN_WRITER");
        _disableAlphaWriter = IsEnabled("SPOUT_DIRECT_SAVER_DISABLE_ALPHA_WRITER");
        _temporaryDirectory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);

        _mainVideoPath = Path.Combine(_temporaryDirectory, "rgb.mp4");
        _alphaVideoPath = Path.Combine(_temporaryDirectory, "alpha.mkv");

        _mainWriter = new MediaFoundationHevcWriter(width, height, frameRate, _mainVideoPath);
        _alphaWriter = new RealtimeGrayFfv1Writer(width, height, frameRate, _alphaVideoPath);

        _channel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writerTask = Task.Factory.StartNew(
            static state =>
            {
                var writer = (RealtimeHybridWriter)state!;
                using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                writer.WriteLoopAsync().GetAwaiter().GetResult();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void QueueFrame(PixelBufferLease bgraFrame, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            bgraFrame.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("Realtime hybrid writer は既に完了しています。");
        }

        _channel.Writer.WriteAsync(new PendingFrame(bgraFrame, repeatCount)).AsTask().GetAwaiter().GetResult();
    }

    public async Task<string> CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return _finalOutputPath;
        }

        _completed = true;
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        if (!_disableMainWriter)
        {
            _mainWriter.Complete();
            _mainWriter.Dispose();
        }

        if (!_disableAlphaWriter)
        {
            await _alphaWriter.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await MuxAsync(cancellationToken).ConfigureAwait(false);
        return _finalOutputPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();

        try
        {
            await _writerTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time writer failures.
        }

        _mainWriter.Dispose();
        await _alphaWriter.DisposeAsync().ConfigureAwait(false);
        TryDeleteTemporaryDirectory();
    }

    private async Task WriteLoopAsync()
    {
        var alphaBuffer = GC.AllocateUninitializedArray<byte>(_pixelCount);

        await foreach (var pending in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!_disableMainWriter)
                {
                    _mainWriter.WriteFrame(pending.BgraFrame.Buffer, pending.BgraFrame.Length, pending.RepeatCount);
                }

                if (!_disableAlphaWriter)
                {
                    ExtractAlphaPlane(pending.BgraFrame.Span, alphaBuffer);
                    _alphaWriter.QueueFrame((byte[])alphaBuffer.Clone(), pending.RepeatCount);
                }
            }
            finally
            {
                pending.BgraFrame.Dispose();
            }
        }
    }

    private async Task MuxAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_finalOutputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = BuildMuxArguments(),
            WorkingDirectory = Path.GetDirectoryName(_finalOutputPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException($"FFmpeg の mux に失敗しました。\n{details.Trim()}");
            }

            TryDeleteTemporaryDirectory();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "ffmpeg を起動できませんでした。PATH の通った ffmpeg.exe を用意してください。",
                ex);
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

    private static void ExtractAlphaPlane(ReadOnlySpan<byte> bgraFrame, Span<byte> alphaDestination)
    {
        var sourceIndex = 3;
        for (var targetIndex = 0; targetIndex < alphaDestination.Length; targetIndex++, sourceIndex += 4)
        {
            alphaDestination[targetIndex] = bgraFrame[sourceIndex];
        }
    }

    private string BuildMuxArguments()
    {
        if (_disableMainWriter && _disableAlphaWriter)
        {
            throw new InvalidOperationException("main/alpha writer の両方が無効になっています。");
        }

        if (_disableAlphaWriter)
        {
            return $"-y -i \"{_mainVideoPath}\" -map 0:v:0 -c copy -cues_to_front 1 \"{_finalOutputPath}\"";
        }

        if (_disableMainWriter)
        {
            return $"-y -i \"{_alphaVideoPath}\" -map 0:v:0 -c copy -cues_to_front 1 \"{_finalOutputPath}\"";
        }

        return $"-y -i \"{_mainVideoPath}\" -i \"{_alphaVideoPath}\" -map 0:v:0 -map 1:v:0 -c copy -cues_to_front 1 \"{_finalOutputPath}\"";
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

    private sealed record PendingFrame(PixelBufferLease BgraFrame, int RepeatCount);
}
