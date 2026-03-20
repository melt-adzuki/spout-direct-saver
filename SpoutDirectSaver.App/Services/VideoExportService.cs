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
    public async Task ExportAsync(
        EncoderOption encoderOption,
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        uint width,
        uint height,
        double outputFrameRate,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = encoderOption.BuildArguments(width, height, outputFrameRate, outputPath),
            WorkingDirectory = Path.GetDirectoryName(spoolPath)!,
            RedirectStandardInput = true,
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

            await WriteRawVideoAsync(
                process.StandardInput.BaseStream,
                spoolPath,
                frames,
                width,
                height,
                outputFrameRate,
                cancellationToken).ConfigureAwait(false);
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
                "ffmpeg を起動できませんでした。PATH の通った ffmpeg.exe を用意してください。",
                ex);
        }
    }

    private static async Task WriteRawVideoAsync(
        Stream destination,
        string spoolPath,
        IReadOnlyList<RecordedFrame> frames,
        uint width,
        uint height,
        double outputFrameRate,
        CancellationToken cancellationToken)
    {
        var frameSize = checked((int)(width * height * 4));
        var rawBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
        byte[] compressedBuffer = Array.Empty<byte>();
        var emittedFrames = 0;
        var accumulatedTimelineFrames = 0.0;

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

                if (frame.SpoolLength <= 0)
                {
                    throw new InvalidOperationException("一時フレームスプールのメタデータが壊れています。");
                }

                if (spoolStream.Position != frame.SpoolOffset)
                {
                    spoolStream.Seek(frame.SpoolOffset, SeekOrigin.Begin);
                }

                if (frame.IsCompressed)
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
                else
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

                accumulatedTimelineFrames += frame.DurationSeconds * outputFrameRate;
                var targetTotalFrames = Math.Max(emittedFrames + 1, (int)Math.Round(accumulatedTimelineFrames));
                var repeatCount = targetTotalFrames - emittedFrames;

                for (var i = 0; i < repeatCount; i++)
                {
                    await destination.WriteAsync(rawBuffer.AsMemory(0, frameSize), cancellationToken).ConfigureAwait(false);
                }

                emittedFrames = targetTotalFrames;
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
}
