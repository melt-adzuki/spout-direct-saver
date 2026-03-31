using System.Diagnostics;
using System.Text.Json;
using K4os.Compression.LZ4;

var options = BenchmarkOptions.Parse(args);
Directory.CreateDirectory(options.OutputRoot);

var summary = new BenchmarkSummary
{
    GeneratedAtUtc = DateTime.UtcNow,
    MachineName = Environment.MachineName,
    ProcessorCount = Environment.ProcessorCount,
    Width = options.Width,
    Height = options.Height,
    FrameRate = options.FrameRate,
    Seconds = options.Seconds,
    FrameCount = options.FrameCount
};

Console.WriteLine($"output_root={options.OutputRoot}");
Console.WriteLine($"resolution={options.Width}x{options.Height}");
Console.WriteLine($"fps={options.FrameRate:0.###}");
Console.WriteLine($"seconds={options.Seconds:0.###}");
Console.WriteLine($"frames={options.FrameCount}");
Console.WriteLine($"processors={Environment.ProcessorCount}");

var cases = BuildCases();
if (options.OnlyCases.Count > 0)
{
    var filteredCases = cases
        .Where(c => options.OnlyCases.Contains(c.Name))
        .ToList();

    if (filteredCases.Count == 0)
    {
        throw new InvalidOperationException("No benchmark cases matched the requested names.");
    }

    cases = filteredCases;
}

foreach (var benchmarkCase in cases)
{
    Console.WriteLine($"Running {benchmarkCase.Name}...");
    var result = await benchmarkCase.RunAsync(options).ConfigureAwait(false);
    summary.Results.Add(result);
    var status = result.Succeeded ? "ok" : "failed";
    Console.WriteLine(
        $"{benchmarkCase.Name}: {status}, elapsed={result.ElapsedSeconds:0.###}s, cpu={result.TotalCpuSeconds:0.###}s, bytes={result.BytesWritten}");
}

var summaryPath = Path.Combine(options.OutputRoot, "summary.json");
await File.WriteAllTextAsync(
    summaryPath,
    JsonSerializer.Serialize(summary, new JsonSerializerOptions
    {
        WriteIndented = true
    })).ConfigureAwait(false);

Console.WriteLine($"SUMMARY_JSON={summaryPath}");

static List<BenchmarkCase> BuildCases()
{
    return
    [
        BenchmarkCase.Custom(
            "alpha_raw_spool",
            "alpha-sidecar",
            ".bin",
            static async (options, outputPath) =>
            {
                var frameBytes = checked(options.Width * options.Height * 4);
                var alphaBytes = checked(options.Width * options.Height);
                var frameBuffer = GC.AllocateUninitializedArray<byte>(frameBytes);
                var alphaBuffer = GC.AllocateUninitializedArray<byte>(alphaBytes);
                var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                var stopwatch = Stopwatch.StartNew();

                await using var output = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan);

                for (var frameIndex = 0; frameIndex < options.FrameCount; frameIndex++)
                {
                    SyntheticFrameGenerator.FillBgraFrame(frameBuffer, options.Width, options.Height, frameIndex);
                    SyntheticFrameGenerator.ExtractAlpha(frameBuffer, alphaBuffer);
                    await output.WriteAsync(alphaBuffer).ConfigureAwait(false);
                }

                await output.FlushAsync().ConfigureAwait(false);
                stopwatch.Stop();
                var cpuAfter = process.TotalProcessorTime;
                var bytesWritten = new FileInfo(outputPath).Length;

                return BenchmarkResult.Success(
                    "alpha_raw_spool",
                    "alpha-sidecar",
                    outputPath,
                    options.FrameCount,
                    stopwatch.Elapsed,
                    cpuAfter - cpuBefore,
                    TimeSpan.Zero,
                    bytesWritten);
            }),
        BenchmarkCase.Custom(
            "alpha_lz4_spool",
            "alpha-sidecar",
            ".bin",
            static async (options, outputPath) =>
            {
                var frameBytes = checked(options.Width * options.Height * 4);
                var alphaBytes = checked(options.Width * options.Height);
                var frameBuffer = GC.AllocateUninitializedArray<byte>(frameBytes);
                var alphaBuffer = GC.AllocateUninitializedArray<byte>(alphaBytes);
                var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                var stopwatch = Stopwatch.StartNew();

                await using var output = new FileStream(
                    outputPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan);

                for (var frameIndex = 0; frameIndex < options.FrameCount; frameIndex++)
                {
                    SyntheticFrameGenerator.FillBgraFrame(frameBuffer, options.Width, options.Height, frameIndex);
                    SyntheticFrameGenerator.ExtractAlpha(frameBuffer, alphaBuffer);
                    var compressed = LZ4Pickler.Pickle(alphaBuffer, LZ4Level.L00_FAST);
                    await output.WriteAsync(compressed).ConfigureAwait(false);
                }

                await output.FlushAsync().ConfigureAwait(false);
                stopwatch.Stop();
                var cpuAfter = process.TotalProcessorTime;
                var bytesWritten = new FileInfo(outputPath).Length;

                return BenchmarkResult.Success(
                    "alpha_lz4_spool",
                    "alpha-sidecar",
                    outputPath,
                    options.FrameCount,
                    stopwatch.Elapsed,
                    cpuAfter - cpuBefore,
                    TimeSpan.Zero,
                    bytesWritten);
            }),
        BenchmarkCase.Ffmpeg(
            "alpha_hevc_gray_mp4",
            "alpha-sidecar",
            ".mp4",
            RawInputKind.Gray,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "gray",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-vf", "format=yuv420p",
                "-c:v", "hevc_nvenc",
                "-preset:v", "p3",
                "-tune:v", "hq",
                "-rc:v", "vbr",
                "-cq:v", "19",
                "-b:v", "0",
                "-bf:v", "0",
                "-g:v", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt", "yuv420p",
                "-profile:v", "main",
                "-movflags", "+faststart",
                "-video_track_timescale", "120000"
            ]),
        BenchmarkCase.Ffmpeg(
            "hevc_bgra_mp4",
            "intermediate",
            ".mp4",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-vf", "format=yuv420p",
                "-c:v", "hevc_nvenc",
                "-preset:v", "p3",
                "-tune:v", "hq",
                "-rc:v", "vbr",
                "-cq:v", "19",
                "-b:v", "0",
                "-bf:v", "0",
                "-g:v", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt", "yuv420p",
                "-profile:v", "main",
                "-movflags", "+faststart",
                "-video_track_timescale", "120000"
            ]),
        BenchmarkCase.Ffmpeg(
            "utvideo_gbrap_avi",
            "intermediate",
            ".avi",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-c:v", "utvideo",
                "-pred", "median",
                "-pix_fmt", "gbrap"
            ]),
        BenchmarkCase.Ffmpeg(
            "png_rgba_mov",
            "intermediate",
            ".mov",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-c:v", "png",
                "-pred", "mixed",
                "-pix_fmt", "rgba",
                "-movflags", "+faststart"
            ]),
        BenchmarkCase.Ffmpeg(
            "prores_4444_mov",
            "intermediate",
            ".mov",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-c:v", "prores_ks",
                "-profile:v", "4444",
                "-pix_fmt", "yuva444p10le",
                "-alpha_bits", "8"
            ]),
        BenchmarkCase.Ffmpeg(
            "hap_alpha_mov",
            "intermediate",
            ".mov",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-c:v", "hap",
                "-format", "hap_alpha",
                "-chunks", "8",
                "-compressor", "snappy"
            ]),
        BenchmarkCase.Ffmpeg(
            "cfhd_mov",
            "intermediate",
            ".mov",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-c:v", "cfhd",
                "-quality", "high",
                "-pix_fmt", "gbrap12le"
            ]),
        BenchmarkCase.Ffmpeg(
            "split_hevc420_nvenc_hevcalpha_mp4",
            "project-default-family",
            ".mp4",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
                "-map", "[rgb]",
                "-c:v:0", "hevc_nvenc",
                "-preset:v:0", "p1",
                "-tune:v:0", "ll",
                "-rc:v:0", "vbr",
                "-cq:v:0", "21",
                "-b:v:0", "0",
                "-g:v:0", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt:v:0", "yuv420p",
                "-profile:v:0", "main",
                "-map", "[aout]",
                "-c:v:1", "hevc_nvenc",
                "-preset:v:1", "p3",
                "-tune:v:1", "hq",
                "-rc:v:1", "vbr",
                "-cq:v:1", "19",
                "-b:v:1", "0",
                "-bf:v:1", "0",
                "-g:v:1", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt:v:1", "yuv420p",
                "-profile:v:1", "main",
                "-movflags", "+faststart",
                "-video_track_timescale", "120000"
            ]),
        BenchmarkCase.Ffmpeg(
            "split_h264420_nvenc_hevcalpha_mp4",
            "project-default-family",
            ".mp4",
            RawInputKind.Bgra,
            static options =>
            [
                "-y",
                "-f", "rawvideo",
                "-pixel_format", "bgra",
                "-video_size", $"{options.Width}x{options.Height}",
                "-framerate", options.FrameRate.ToString(Culture.Invariant),
                "-i", "-",
                "-an",
                "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
                "-map", "[rgb]",
                "-c:v:0", "h264_nvenc",
                "-preset:v:0", "p1",
                "-tune:v:0", "ll",
                "-rc:v:0", "vbr",
                "-cq:v:0", "19",
                "-b:v:0", "0",
                "-g:v:0", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt:v:0", "yuv420p",
                "-profile:v:0", "high",
                "-map", "[aout]",
                "-c:v:1", "hevc_nvenc",
                "-preset:v:1", "p3",
                "-tune:v:1", "hq",
                "-rc:v:1", "vbr",
                "-cq:v:1", "19",
                "-b:v:1", "0",
                "-bf:v:1", "0",
                "-g:v:1", Math.Max(1, (int)Math.Round(options.FrameRate)).ToString(Culture.Invariant),
                "-pix_fmt:v:1", "yuv420p",
                "-profile:v:1", "main",
                "-movflags", "+faststart",
                "-video_track_timescale", "120000"
            ])
    ];
}

static class Culture
{
    public static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;
}

enum RawInputKind
{
    Bgra,
    Gray
}

sealed record BenchmarkCase(
    string Name,
    string Category,
    string Extension,
    Func<BenchmarkOptions, Task<BenchmarkResult>> RunAsync)
{
    public static BenchmarkCase Custom(
        string name,
        string category,
        string extension,
        Func<BenchmarkOptions, string, Task<BenchmarkResult>> runner)
    {
        return new BenchmarkCase(
            name,
            category,
            extension,
            options =>
            {
                var outputPath = Path.Combine(options.OutputRoot, name + extension);
                return runner(options, outputPath);
            });
    }

    public static BenchmarkCase Ffmpeg(
        string name,
        string category,
        string extension,
        RawInputKind inputKind,
        Func<BenchmarkOptions, IReadOnlyList<string>> argumentsFactory)
    {
        return new BenchmarkCase(
            name,
            category,
            extension,
            options => RunFfmpegCaseAsync(
                name,
                category,
                extension,
                inputKind,
                argumentsFactory(options),
                options));
    }

    private static async Task<BenchmarkResult> RunFfmpegCaseAsync(
        string name,
        string category,
        string extension,
        RawInputKind inputKind,
        IReadOnlyList<string> arguments,
        BenchmarkOptions options)
    {
        var outputPath = Path.Combine(options.OutputRoot, name + extension);
        var logPath = Path.Combine(options.OutputRoot, name + ".log");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.ArgumentList.Add(outputPath);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            var selfProcess = Process.GetCurrentProcess();
            var selfCpuBefore = selfProcess.TotalProcessorTime;
            var stopwatch = Stopwatch.StartNew();

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await WriteFfmpegInputAsync(
                process.StandardInput.BaseStream,
                inputKind,
                options).ConfigureAwait(false);
            process.StandardInput.Close();

            await process.WaitForExitAsync().ConfigureAwait(false);
            stopwatch.Stop();
            var selfCpuAfter = selfProcess.TotalProcessorTime;
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            await File.WriteAllTextAsync(logPath, stderr + Environment.NewLine + stdout).ConfigureAwait(false);

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                return BenchmarkResult.Failure(
                    name,
                    category,
                    outputPath,
                    options.FrameCount,
                    stopwatch.Elapsed,
                    selfCpuAfter - selfCpuBefore,
                    SafeGetCpuTime(process),
                    stderr);
            }

            var bytesWritten = new FileInfo(outputPath).Length;
            return BenchmarkResult.Success(
                name,
                category,
                outputPath,
                options.FrameCount,
                stopwatch.Elapsed,
                selfCpuAfter - selfCpuBefore,
                SafeGetCpuTime(process),
                bytesWritten);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(logPath, ex.ToString()).ConfigureAwait(false);
            return BenchmarkResult.Failure(
                name,
                category,
                outputPath,
                options.FrameCount,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                ex.Message);
        }
    }

    private static TimeSpan SafeGetCpuTime(Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    private static async Task WriteFfmpegInputAsync(
        Stream destination,
        RawInputKind inputKind,
        BenchmarkOptions options)
    {
        var frameBytes = checked(options.Width * options.Height * 4);
        var alphaBytes = checked(options.Width * options.Height);
        var frameBuffer = GC.AllocateUninitializedArray<byte>(frameBytes);
        var alphaBuffer = inputKind == RawInputKind.Gray
            ? GC.AllocateUninitializedArray<byte>(alphaBytes)
            : Array.Empty<byte>();

        for (var frameIndex = 0; frameIndex < options.FrameCount; frameIndex++)
        {
            SyntheticFrameGenerator.FillBgraFrame(frameBuffer, options.Width, options.Height, frameIndex);
            if (inputKind == RawInputKind.Gray)
            {
                SyntheticFrameGenerator.ExtractAlpha(frameBuffer, alphaBuffer);
                await destination.WriteAsync(alphaBuffer).ConfigureAwait(false);
            }
            else
            {
                await destination.WriteAsync(frameBuffer).ConfigureAwait(false);
            }
        }

        await destination.FlushAsync().ConfigureAwait(false);
    }
}

sealed class BenchmarkOptions
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required double FrameRate { get; init; }
    public required double Seconds { get; init; }
    public required string OutputRoot { get; init; }
    public required HashSet<string> OnlyCases { get; init; }

    public int FrameCount => Math.Max(1, (int)Math.Round(FrameRate * Seconds));

    public static BenchmarkOptions Parse(string[] args)
    {
        var width = 3840;
        var height = 2160;
        var frameRate = 60.0;
        var seconds = 2.0;
        var outputRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpoutDirectSaver",
            "CodecBench",
            DateTime.Now.ToString("yyyyMMdd_HHmmss", Culture.Invariant));
        var onlyCases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--width":
                    width = int.Parse(args[++index], Culture.Invariant);
                    break;
                case "--height":
                    height = int.Parse(args[++index], Culture.Invariant);
                    break;
                case "--fps":
                    frameRate = double.Parse(args[++index], Culture.Invariant);
                    break;
                case "--seconds":
                    seconds = double.Parse(args[++index], Culture.Invariant);
                    break;
                case "--output-root":
                    outputRoot = args[++index];
                    break;
                case "--only":
                    foreach (var name in args[++index].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        onlyCases.Add(name);
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unknown argument: {args[index]}");
            }
        }

        if (width <= 0 || height <= 0 || frameRate <= 0 || seconds <= 0)
        {
            throw new InvalidOperationException("Width, height, fps, and seconds must be positive.");
        }

        return new BenchmarkOptions
        {
            Width = width,
            Height = height,
            FrameRate = frameRate,
            Seconds = seconds,
            OutputRoot = outputRoot,
            OnlyCases = onlyCases
        };
    }
}

sealed class BenchmarkSummary
{
    public DateTime GeneratedAtUtc { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public double Seconds { get; set; }
    public int FrameCount { get; set; }
    public List<BenchmarkResult> Results { get; } = [];
}

sealed class BenchmarkResult
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public int Frames { get; set; }
    public bool Succeeded { get; set; }
    public double ElapsedSeconds { get; set; }
    public double SelfCpuSeconds { get; set; }
    public double ChildCpuSeconds { get; set; }
    public double TotalCpuSeconds { get; set; }
    public double AvgCpuUsageAllCoresPct { get; set; }
    public long BytesWritten { get; set; }
    public double MebiBytesWritten { get; set; }
    public double FramesPerSecond { get; set; }
    public double WriteMebiBytesPerSecond { get; set; }
    public string? Error { get; set; }

    public static BenchmarkResult Success(
        string name,
        string category,
        string outputPath,
        int frames,
        TimeSpan elapsed,
        TimeSpan selfCpu,
        TimeSpan childCpu,
        long bytesWritten)
    {
        var totalCpu = selfCpu + childCpu;
        var elapsedSeconds = elapsed.TotalSeconds;
        return new BenchmarkResult
        {
            Name = name,
            Category = category,
            OutputPath = outputPath,
            Frames = frames,
            Succeeded = true,
            ElapsedSeconds = elapsedSeconds,
            SelfCpuSeconds = selfCpu.TotalSeconds,
            ChildCpuSeconds = childCpu.TotalSeconds,
            TotalCpuSeconds = totalCpu.TotalSeconds,
            AvgCpuUsageAllCoresPct = elapsedSeconds > 0
                ? (totalCpu.TotalSeconds / (elapsedSeconds * Math.Max(1, Environment.ProcessorCount))) * 100.0
                : 0.0,
            BytesWritten = bytesWritten,
            MebiBytesWritten = bytesWritten / 1048576.0,
            FramesPerSecond = elapsedSeconds > 0 ? frames / elapsedSeconds : 0.0,
            WriteMebiBytesPerSecond = elapsedSeconds > 0 ? (bytesWritten / 1048576.0) / elapsedSeconds : 0.0
        };
    }

    public static BenchmarkResult Failure(
        string name,
        string category,
        string outputPath,
        int frames,
        TimeSpan elapsed,
        TimeSpan selfCpu,
        TimeSpan childCpu,
        string error)
    {
        var result = Success(name, category, outputPath, frames, elapsed, selfCpu, childCpu, 0);
        result.Succeeded = false;
        result.Error = error;
        return result;
    }
}

static class SyntheticFrameGenerator
{
    public static void FillBgraFrame(byte[] destination, int width, int height, int frameIndex)
    {
        var stride = checked(width * 4);
        var framePhase = frameIndex * 17;

        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * stride;
            var coarseY = y >> 3;
            var yWave = (coarseY * 19) + (framePhase * 3);

            for (var x = 0; x < width; x++)
            {
                var coarseX = x >> 3;
                var offset = rowOffset + (x * 4);
                var band = ((coarseX * 13) + yWave) & 0xFF;
                var cross = ((coarseX * 7) ^ (coarseY * 11) ^ (framePhase * 5)) & 0xFF;
                var pulse = ((coarseX + framePhase) >> 1) + ((coarseY - framePhase) >> 2);
                var alpha = 24 + ((band >> 1) + (cross >> 2) + (pulse & 0x3F));

                destination[offset] = (byte)((band + (frameIndex * 5)) & 0xFF);
                destination[offset + 1] = (byte)((cross + (coarseY * 9)) & 0xFF);
                destination[offset + 2] = (byte)(((coarseX * 17) + (frameIndex * 11) + (coarseY * 3)) & 0xFF);
                destination[offset + 3] = (byte)Math.Clamp(alpha, 0, 255);
            }
        }
    }

    public static void ExtractAlpha(byte[] bgraSource, byte[] alphaDestination)
    {
        var sourceIndex = 3;
        for (var index = 0; index < alphaDestination.Length; index++, sourceIndex += 4)
        {
            alphaDestination[index] = bgraSource[sourceIndex];
        }
    }
}
