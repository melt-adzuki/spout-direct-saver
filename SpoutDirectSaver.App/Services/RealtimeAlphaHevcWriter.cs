using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeAlphaHevcWriter : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream _pipeServer;
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _writeTask;
    private readonly Task<string> _stdoutTask;
    private readonly Task<string> _stderrTask;
    private readonly Task _pipeConnectionTask;
    private bool _completed;
    private bool _disposed;
    private bool _pipeClosed;

    public RealtimeAlphaHevcWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        int queueCapacity = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var pipeName = $"SpoutDirectSaverAlpha_{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{pipeName}";

        _channel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            0,
            8 * 1024 * 1024);
        _pipeConnectionTask = _pipeServer.WaitForConnectionAsync();
        var gop = Math.Max(1, (int)Math.Round(frameRate));

        var arguments =
            $"-y -f rawvideo -pixel_format gray -video_size {width}x{height} -framerate {frameRate:0.###} -blocksize 16777216 -i \"{pipePath}\" -an -vf format=yuv420p -c:v hevc_nvenc -preset:v p3 -tune:v hq -rc:v vbr -cq:v 19 -b:v 0 -bf:v 0 -g:v {gop} -pix_fmt:v yuv420p -profile:v main -movflags +faststart -video_track_timescale 120000 \"{outputPath}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
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

        _stdoutTask = _process.StandardOutput.ReadToEndAsync();
        _stderrTask = _process.StandardError.ReadToEndAsync();
        _writeTask = Task.Factory.StartNew(
            static state =>
            {
                var writer = (RealtimeAlphaHevcWriter)state!;
                using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                writer.WriteLoopAsync().GetAwaiter().GetResult();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void QueueFrame(byte[] pixelData, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("Realtime alpha writer は既に完了しています。");
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
        await ClosePipeAsync(cancellationToken).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await _stdoutTask.ConfigureAwait(false);
        var stderr = await _stderrTask.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException($"FFmpeg の alpha 書き出しに失敗しました。\n{details.Trim()}");
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
            await ClosePipeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time pipe close failures.
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
        await _pipeConnectionTask.ConfigureAwait(false);
        await foreach (var pending in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            for (var index = 0; index < pending.RepeatCount; index++)
            {
                await _pipeServer.WriteAsync(pending.PixelData.AsMemory(0, pending.PixelData.Length)).ConfigureAwait(false);
            }
        }

        await _pipeServer.FlushAsync().ConfigureAwait(false);
    }

    private async Task ClosePipeAsync(CancellationToken cancellationToken)
    {
        if (_pipeClosed)
        {
            return;
        }

        _pipeClosed = true;
        if (_pipeServer.IsConnected)
        {
            await _pipeServer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _pipeServer.Dispose();
    }

    private sealed record PendingFrame(byte[] PixelData, int RepeatCount);
}
