using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class VideoExportService
{
    internal async Task ExportCapturedTakeAsync(
        CapturedTake take,
        string outputPath,
        IProgress<EncodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(take);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var hasSidecar = take.HasSidecar;
        var sidecarOutputPath = hasSidecar ? BuildAlphaSidecarPath(outputPath) : null;
        var mainPartialPath = outputPath + ".partial";
        var sidecarPartialPath = sidecarOutputPath is null ? null : sidecarOutputPath + ".partial";
        var mainLength = new FileInfo(take.PreviewVideoPath).Length;
        var sidecarLength = hasSidecar ? new FileInfo(take.PreviewSidecarPath!).Length : 0;
        var totalBytes = Math.Max(mainLength + sidecarLength, 1);

        try
        {
            progress?.Report(new EncodeProgress(0, "preparing", "Encoding..."));

            await CopyFileAsync(
                take.PreviewVideoPath,
                mainPartialPath,
                bytesCompletedBeforeThisFile: 0,
                totalBytes,
                progress,
                "copying video",
                cancellationToken).ConfigureAwait(false);

            if (hasSidecar && sidecarOutputPath is not null && sidecarPartialPath is not null)
            {
                await CopyFileAsync(
                    take.PreviewSidecarPath!,
                    sidecarPartialPath,
                    bytesCompletedBeforeThisFile: mainLength,
                    totalBytes,
                    progress,
                    "copying alpha",
                    cancellationToken).ConfigureAwait(false);
            }

            MoveFile(mainPartialPath, outputPath);
            if (hasSidecar && sidecarOutputPath is not null && sidecarPartialPath is not null)
            {
                MoveFile(sidecarPartialPath, sidecarOutputPath);
            }

            progress?.Report(new EncodeProgress(100, "done", "Done!"));
        }
        catch
        {
            TryDeleteFile(outputPath);
            if (sidecarOutputPath is not null)
            {
                TryDeleteFile(sidecarOutputPath);
            }

            TryDeleteFile(mainPartialPath);
            if (sidecarPartialPath is not null)
            {
                TryDeleteFile(sidecarPartialPath);
            }

            throw;
        }
    }

    public async Task ExportAlphaTrackAsync(
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        uint width,
        uint height,
        double outputFrameRate,
        string outputPath,
        CancellationToken cancellationToken,
        IProgress<EncodeProgress>? progress = null,
        AlphaNvencEncoderSettings? settings = null)
    {
        await WaitForStableFileAsync(spoolPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var effectiveSettings = settings ?? EncoderSettingsRoot.CreateDefaults().Alpha;

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = effectiveSettings.BuildArguments(
                width,
                height,
                outputFrameRate,
                "-i -",
                outputPath),
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await RunFfmpegWithRawInputAsync(
            startInfo,
            destination => WriteRawVideoAsync(
                destination,
                spoolPath,
                frames,
                checked((int)(width * height)),
                outputFrameRate,
                progress,
                "Encoding alpha sidecar",
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task MuxVideoAndAlphaAsync(
        string rgbVideoPath,
        string alphaVideoPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await WaitForStableFileAsync(rgbVideoPath, cancellationToken).ConfigureAwait(false);
        await WaitForStableFileAsync(alphaVideoPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                $"-y -i \"{rgbVideoPath}\" -i \"{alphaVideoPath}\" -map 0:v:0 -map 1:v:0 -c copy -movflags +faststart \"{outputPath}\"",
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await RunFfmpegAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemuxSingleVideoAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await WaitForStableFileAsync(inputPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments =
                $"-y -i \"{inputPath}\" -map 0:v:0 -c copy -movflags +faststart \"{outputPath}\"",
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await RunFfmpegAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(
        EncoderOption encoderOption,
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        uint width,
        uint height,
        double outputFrameRate,
        string outputPath,
        CancellationToken cancellationToken)
        => await ExportAsync(encoderOption, spoolPath, frames, width, height, outputFrameRate, outputPath, cancellationToken, null).ConfigureAwait(false);

    public async Task ExportAsync(
        EncoderOption encoderOption,
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        uint width,
        uint height,
        double outputFrameRate,
        string outputPath,
        CancellationToken cancellationToken,
        IProgress<EncodeProgress>? progress)
    {
        await WaitForStableFileAsync(spoolPath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = encoderOption.BuildArguments(width, height, outputFrameRate, outputPath),
            WorkingDirectory = Path.GetDirectoryName(outputPath)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        await RunFfmpegWithRawInputAsync(
            startInfo,
            destination => WriteRawVideoAsync(
                destination,
                spoolPath,
                frames,
                checked((int)(width * height * 4)),
                outputFrameRate,
                progress,
                "Encoding video",
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteRawVideoAsync(
        Stream destination,
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        int frameSize,
        double outputFrameRate,
        IProgress<EncodeProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        var rawBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
        byte[] compressedBuffer = Array.Empty<byte>();
        var emittedFrames = 0;
        var accumulatedTimelineFrames = 0.0;
        var hasPreviousDecodedFrame = false;
        var lastReportedPercent = -1;
        var totalFrames = Math.Max(frames.Count, 1);
        var processedFrames = 0;

        try
        {
            await using var spoolStream = new FileStream(
                spoolPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan);

            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (frame.ReusePreviousSpoolFrame)
                {
                    if (!hasPreviousDecodedFrame)
                    {
                        throw new InvalidOperationException("前フレーム再利用を要求されましたが、復元済みフレームがありません。");
                    }
                }
                else if (frame.SpoolLength <= 0)
                {
                    throw new InvalidOperationException("一時フレームスプールのメタデータが壊れています。");
                }

                if (!frame.ReusePreviousSpoolFrame && spoolStream.Position != frame.SpoolOffset)
                {
                    spoolStream.Seek(frame.SpoolOffset, SeekOrigin.Begin);
                }

                if (!frame.ReusePreviousSpoolFrame && frame.IsCompressed)
                {
                    if (compressedBuffer.Length < frame.SpoolLength)
                    {
                        if (compressedBuffer.Length > 0)
                        {
                            ArrayPool<byte>.Shared.Return(compressedBuffer);
                        }

                        compressedBuffer = ArrayPool<byte>.Shared.Rent(frame.SpoolLength);
                    }

                    await ReadExactAsync(
                        spoolStream,
                        compressedBuffer,
                        frame.SpoolLength,
                        cancellationToken).ConfigureAwait(false);

                    InflateFrame(
                        compressedBuffer.AsSpan(0, frame.SpoolLength),
                        rawBuffer,
                        frameSize);
                }
                else if (!frame.ReusePreviousSpoolFrame)
                {
                    if (frame.SpoolLength != frameSize)
                    {
                        throw new InvalidOperationException("raw スプールフレームのサイズが想定と一致しません。");
                    }

                    await ReadExactAsync(
                        spoolStream,
                        rawBuffer,
                        frameSize,
                        cancellationToken).ConfigureAwait(false);
                }

                hasPreviousDecodedFrame = true;

                accumulatedTimelineFrames += frame.DurationSeconds * outputFrameRate;
                var targetTotalFrames = Math.Max(emittedFrames + 1, (int)Math.Round(accumulatedTimelineFrames));
                var repeatCount = targetTotalFrames - emittedFrames;

                for (var i = 0; i < repeatCount; i++)
                {
                    await destination.WriteAsync(rawBuffer.AsMemory(0, frameSize), cancellationToken).ConfigureAwait(false);
                }

                emittedFrames = targetTotalFrames;
                processedFrames++;
                var percent = (int)Math.Round(processedFrames * 100.0 / totalFrames);
                if (percent != lastReportedPercent)
                {
                    progress?.Report(new EncodeProgress(Math.Clamp(percent, 0, 100), phase, "Encoding..."));
                    lastReportedPercent = percent;
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rawBuffer);
            if (compressedBuffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(compressedBuffer);
            }
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        long bytesCompletedBeforeThisFile,
        long totalBytes,
        IProgress<EncodeProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.SequentialScan);

            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.SequentialScan);

            long copiedInFile = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copiedInFile += read;
                var percent = (int)Math.Round((bytesCompletedBeforeThisFile + copiedInFile) * 100.0 / totalBytes);
                progress?.Report(new EncodeProgress(Math.Clamp(percent, 0, 100), phase, "Encoding..."));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void MoveFile(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(sourcePath, destinationPath);
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string BuildAlphaSidecarPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(outputPath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}.alpha.mp4");
    }

    private static async Task WaitForStableFileAsync(string path, CancellationToken cancellationToken)
    {
        DebugTrace.WriteLine("VideoExportService", $"wait start path={path}");
        const int maxAttempts = 300;
        const int requiredStableObservations = 3;
        long lastLength = -1;
        var stableObservations = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Exists && fileInfo.Length > 0)
                {
                    if (fileInfo.Length == lastLength)
                    {
                        stableObservations++;
                    }
                    else
                    {
                        stableObservations = 1;
                        lastLength = fileInfo.Length;
                    }

                    if (stableObservations >= requiredStableObservations)
                    {
                        DebugTrace.WriteLine("VideoExportService", $"wait success path={path} size={fileInfo.Length}");
                        return;
                    }
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        DebugTrace.WriteLine("VideoExportService", $"wait fail path={path}");
        throw new FileNotFoundException($"入力ファイルの準備完了を待てませんでした: {path}", path);
    }

    private static async Task ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int bytesToRead,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, bytesToRead - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("一時フレームスプールの読み込みが途中で終了しました。");
            }

            totalRead += read;
        }
    }

    private static void InflateFrame(
        ReadOnlySpan<byte> compressedFrame,
        byte[] destinationBuffer,
        int expectedBytes)
    {
        if (LZ4Pickler.UnpickledSize(compressedFrame) != expectedBytes)
        {
            throw new InvalidOperationException("LZ4 スプールフレームの復元サイズが想定と一致しません。");
        }

        LZ4Pickler.Unpickle(
            compressedFrame,
            destinationBuffer.AsSpan(0, expectedBytes));
    }

    private static async Task RunFfmpegWithRawInputAsync(
        ProcessStartInfo startInfo,
        Func<Stream, Task> inputWriter,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = startInfo
            };

            process.Start();
            await inputWriter(process.StandardInput.BaseStream).ConfigureAwait(false);
            process.StandardInput.Close();

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
                $"ffmpeg を起動できませんでした。WorkingDirectory={startInfo.WorkingDirectory}, Message={ex.Message}",
                ex);
        }
    }

    private static async Task RunFfmpegAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
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
                throw new InvalidOperationException($"FFmpeg の処理に失敗しました。\n{details.Trim()}");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"ffmpeg を起動できませんでした。WorkingDirectory={startInfo.WorkingDirectory}, Message={ex.Message}",
                ex);
        }
    }
}
