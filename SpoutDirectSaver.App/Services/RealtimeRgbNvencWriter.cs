using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeRgbNvencWriter : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _writeTask;
    private readonly Task<string> _stdoutTask;
    private readonly Task<string> _stderrTask;
    private readonly Stream _destination;
    private bool _completed;
    private bool _disposed;
    private bool _inputClosed;

    public RealtimeRgbNvencWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        int queueCapacity = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var gop = Math.Max(1, (int)Math.Round(frameRate));

        _channel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {frameRate:0.###} -i - -an -c:v hevc_nvenc -preset:v p1 -tune:v ll -rc:v vbr -cq:v 21 -b:v 0 -g:v {gop} -pix_fmt:v yuv420p -profile:v main \"{outputPath}\"",
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process
            {
                StartInfo = startInfo
            };

            _process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "ffmpeg を起動できませんでした。PATH の通った ffmpeg.exe を用意してください。",
                ex);
        }

        _destination = _process.StandardInput.BaseStream;
        _stdoutTask = _process.StandardOutput.ReadToEndAsync();
        _stderrTask = _process.StandardError.ReadToEndAsync();
        _writeTask = Task.Factory.StartNew(
            static state =>
            {
                var writer = (RealtimeRgbNvencWriter)state!;
                using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                writer.WriteLoopAsync().GetAwaiter().GetResult();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void QueueFrame(PixelBufferLease pixelData, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            pixelData.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            pixelData.Dispose();
            throw new InvalidOperationException("Realtime RGB writer は既に完了しています。");
        }

        _channel.Writer.WriteAsync(new PendingFrame(pixelData, repeatCount)).AsTask().GetAwaiter().GetResult();
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _channel.Writer.TryComplete();

        await _writeTask.ConfigureAwait(false);
        await CloseInputAsync(cancellationToken).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await _stdoutTask.ConfigureAwait(false);
        var stderr = await _stderrTask.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"FFmpeg の RGB realtime 書き出しに失敗しました。\n{details.Trim()}");
        }
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
            await _writeTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time writer failures.
        }

        try
        {
            await CloseInputAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time input close failures.
        }

        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup-time kill failures.
            }
        }

        _process.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var pending in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                for (var index = 0; index < pending.RepeatCount; index++)
                {
                    await _destination.WriteAsync(
                        pending.PixelData.Buffer.AsMemory(0, pending.PixelData.Length)).ConfigureAwait(false);
                }
            }
            finally
            {
                pending.PixelData.Dispose();
            }
        }

        await _destination.FlushAsync().ConfigureAwait(false);
    }

    private async Task CloseInputAsync(CancellationToken cancellationToken)
    {
        if (_inputClosed)
        {
            return;
        }

        _inputClosed = true;
        await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        _process.StandardInput.Close();
    }

    private sealed record PendingFrame(PixelBufferLease PixelData, int RepeatCount);
}
