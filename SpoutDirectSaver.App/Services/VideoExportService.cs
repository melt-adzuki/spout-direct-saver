using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class VideoExportService
{
    public async Task ExportAsync(
        EncoderOption encoderOption,
        string manifestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = encoderOption.BuildArguments(manifestPath, outputPath),
            WorkingDirectory = Path.GetDirectoryName(manifestPath)!,
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

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"FFmpeg の書き出しに失敗しました。\n{details.Trim()}");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "ffmpeg を起動できませんでした。PATH の通った ffmpeg.exe を用意してください。",
                ex);
        }
    }
}
