using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SpoutDirectSaver.E2E;

internal static class ContentVideoAnalysis
{
    public static async Task<ContentAnalysisReport> AnalyzeAsync(string inputPath, int analysisWidth = 64, string? csvPath = null)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Input file was not found.", inputPath);
        }

        var probe = await VideoProbe.ReadAsync(inputPath).ConfigureAwait(false);
        var analysisHeight = Math.Max(1, (int)Math.Round(probe.Height * (analysisWidth / (double)probe.Width)));
        var samples = await DecodeAndAnalyzeAsync(inputPath, analysisWidth, analysisHeight, probe.FramePtsSeconds).ConfigureAwait(false);
        if (samples.Count == 0)
        {
            throw new InvalidOperationException("No frames were decoded.");
        }

        var diffScores = samples
            .Skip(1)
            .Select(sample => sample.SignatureMeanAbsoluteDifference)
            .ToArray();

        var otsuThreshold = diffScores.Length == 0 ? 0.0 : OtsuThreshold.Compute(diffScores);
        var motionThreshold = diffScores.Length == 0 ? 0.0 : Quantiles.Compute(diffScores.Where(value => value > 0.0).ToArray(), 0.25);
        var exactStats = VisualUpdateStats.Build(
            samples,
            static sample => sample.IsExactSignatureRepeat,
            "exact_signature");
        var motionStutterStats = VisualUpdateStats.BuildMotionStutter(
            samples,
            probe.AverageFrameRate,
            motionThreshold,
            "motion_stutter");
        var perceptualStats = VisualUpdateStats.Build(
            samples,
            sample => sample.SignatureMeanAbsoluteDifference <= otsuThreshold,
            "experimental_perceptual");

        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            await WriteCsvAsync(csvPath!, samples, otsuThreshold).ConfigureAwait(false);
        }

        return new ContentAnalysisReport(
            inputPath,
            probe.CodecName,
            probe.Width,
            probe.Height,
            probe.AverageFrameRate,
            analysisWidth,
            analysisHeight,
            motionThreshold,
            otsuThreshold,
            exactStats,
            motionStutterStats,
            perceptualStats,
            csvPath);
    }

    private static async Task<List<FrameSample>> DecodeAndAnalyzeAsync(
        string inputPath,
        int analysisWidth,
        int analysisHeight,
        IReadOnlyList<double> framePtsSeconds)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments =
                    $"-v error -nostdin -i \"{inputPath}\" -vf \"scale={analysisWidth}:{analysisHeight}:flags=area,format=gray\" -pix_fmt gray -f rawvideo -",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();

        var frameSize = checked(analysisWidth * analysisHeight);
        var stdout = process.StandardOutput.BaseStream;
        var previousFrame = ArrayPool<byte>.Shared.Rent(frameSize);
        var currentFrame = ArrayPool<byte>.Shared.Rent(frameSize);
        var previousSignature = ArrayPool<byte>.Shared.Rent(SignatureBuilder.SignatureLength);
        var currentSignatureBuffer = ArrayPool<byte>.Shared.Rent(SignatureBuilder.SignatureLength);
        var samples = new List<FrameSample>(framePtsSeconds.Count);
        var frameIndex = 0;
        var hasPrevious = false;

        try
        {
            while (true)
            {
                var bytesRead = await ReadFullFrameAsync(stdout, currentFrame.AsMemory(0, frameSize)).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    break;
                }

                if (bytesRead != frameSize)
                {
                    throw new InvalidOperationException($"Unexpected frame size. Expected {frameSize} bytes but got {bytesRead}.");
                }

                var currentSignature = currentSignatureBuffer.AsSpan(0, SignatureBuilder.SignatureLength);
                SignatureBuilder.Build(currentFrame.AsSpan(0, frameSize), analysisWidth, analysisHeight, currentSignature);

                var ptsSeconds = frameIndex < framePtsSeconds.Count
                    ? framePtsSeconds[frameIndex]
                    : frameIndex;

                if (!hasPrevious)
                {
                    currentSignature.CopyTo(previousSignature);
                    currentFrame.AsSpan(0, frameSize).CopyTo(previousFrame);
                    samples.Add(new FrameSample(frameIndex, ptsSeconds, false, 0.0));
                    hasPrevious = true;
                    frameIndex++;
                    continue;
                }

                var exactSignatureRepeat = currentSignature.SequenceEqual(previousSignature.AsSpan(0, SignatureBuilder.SignatureLength));
                var meanAbsoluteDifference = ComputeSignatureDifference(
                    currentSignature,
                    previousSignature.AsSpan(0, SignatureBuilder.SignatureLength));

                samples.Add(new FrameSample(frameIndex, ptsSeconds, exactSignatureRepeat, meanAbsoluteDifference));
                currentSignature.CopyTo(previousSignature);
                currentFrame.AsSpan(0, frameSize).CopyTo(previousFrame);
                frameIndex++;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {stderr}");
            }

            return samples;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(previousFrame);
            ArrayPool<byte>.Shared.Return(currentFrame);
            ArrayPool<byte>.Shared.Return(previousSignature);
            ArrayPool<byte>.Shared.Return(currentSignatureBuffer);
        }
    }

    private static double ComputeSignatureDifference(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        long total = 0;
        for (var index = 0; index < current.Length; index++)
        {
            total += Math.Abs(current[index] - previous[index]);
        }

        return total / (double)current.Length;
    }

    private static async Task<int> ReadFullFrameAsync(Stream stream, Memory<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[totalRead..]).ConfigureAwait(false);
            if (read == 0)
            {
                return totalRead;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static async Task WriteCsvAsync(string path, IReadOnlyList<FrameSample> samples, double perceptualThreshold)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync("frame_index,pts_seconds,exact_signature_repeat,signature_mean_abs_diff,perceptual_repeat").ConfigureAwait(false);

        foreach (var sample in samples)
        {
            await writer.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sample.FrameIndex},{sample.PtsSeconds:0.######},{sample.IsExactSignatureRepeat},{sample.SignatureMeanAbsoluteDifference:0.######},{sample.SignatureMeanAbsoluteDifference <= perceptualThreshold}")).ConfigureAwait(false);
        }
    }
}

internal sealed record ContentAnalysisReport(
    string InputPath,
    string Codec,
    int Width,
    int Height,
    double NominalFps,
    int AnalysisWidth,
    int AnalysisHeight,
    double MotionThreshold,
    double PerceptualThreshold,
    VisualUpdateStats ExactSignature,
    VisualUpdateStats MotionStutter,
    VisualUpdateStats ExperimentalPerceptual,
    string? CsvPath)
{
    public void PrintToConsole()
    {
        Console.WriteLine($"content_analysis_input={InputPath}");
        Console.WriteLine($"content_analysis_codec={Codec}");
        Console.WriteLine($"content_analysis_size={Width}x{Height}");
        Console.WriteLine($"content_analysis_nominal_fps={NominalFps:0.###}");
        Console.WriteLine($"content_analysis_analysis_size={AnalysisWidth}x{AnalysisHeight}");
        PrintStats("exact_signature", ExactSignature);
        Console.WriteLine($"content_analysis_motion_threshold={MotionThreshold:0.###}");
        PrintStats("motion_stutter", MotionStutter);
        Console.WriteLine($"content_analysis_experimental_perceptual_threshold={PerceptualThreshold:0.###}");
        PrintStats("experimental_perceptual", ExperimentalPerceptual);
        if (!string.IsNullOrWhiteSpace(CsvPath))
        {
            Console.WriteLine($"content_analysis_csv={CsvPath}");
        }
    }

    private static void PrintStats(string label, VisualUpdateStats stats)
    {
        Console.WriteLine($"content_analysis_{label}_avg_fps={stats.AverageFps:0.00}");
        Console.WriteLine($"content_analysis_{label}_min_1s_fps={stats.MinimumOneSecondFps:0.00}");
        Console.WriteLine($"content_analysis_{label}_repeat_ratio={stats.RepeatFrameRatio:0.000}");
        Console.WriteLine($"content_analysis_{label}_update_count={stats.UpdateCount}");
        Console.WriteLine($"content_analysis_{label}_longest_repeat_run={stats.LongestRepeatRunFrames}");
        for (var index = 0; index < stats.WorstWindows.Count; index++)
        {
            var window = stats.WorstWindows[index];
            Console.WriteLine(
                $"content_analysis_{label}_worst_window_{index + 1}={window.StartSeconds:0.###}-{window.EndSeconds:0.###}s fps={window.FramesPerSecond:0.00}");
        }
    }
}

internal sealed record VideoProbe(int Width, int Height, double AverageFrameRate, string CodecName, IReadOnlyList<double> FramePtsSeconds)
{
    public static async Task<VideoProbe> ReadAsync(string inputPath)
    {
        var streamJson = await RunProcessAsync(
            "ffprobe",
            $"-v error -select_streams v:0 -show_entries stream=width,height,avg_frame_rate,codec_name -of json \"{inputPath}\"").ConfigureAwait(false);
        using var streamDocument = JsonDocument.Parse(streamJson);
        var stream = streamDocument.RootElement.GetProperty("streams")[0];

        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        var averageFrameRate = ParseFrameRate(stream.GetProperty("avg_frame_rate").GetString());
        var codecName = stream.GetProperty("codec_name").GetString() ?? "(unknown)";

        var frameCsv = await RunProcessAsync(
            "ffprobe",
            $"-v error -select_streams v:0 -show_entries frame=pts_time -of csv=p=0 \"{inputPath}\"").ConfigureAwait(false);
        var framePts = frameCsv
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => double.Parse(line.Split(',')[0], CultureInfo.InvariantCulture))
            .ToArray();

        return new VideoProbe(width, height, averageFrameRate, codecName, framePts);
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} exited with code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static double ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0.0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0.0)
        {
            return numerator / denominator;
        }

        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}

internal static class SignatureBuilder
{
    public const int Columns = 16;
    public const int Rows = 9;
    public const int SignatureLength = Columns * Rows;

    public static void Build(ReadOnlySpan<byte> frame, int width, int height, Span<byte> signature)
    {
        signature.Clear();
        Span<int> totals = stackalloc int[SignatureLength];
        Span<int> counts = stackalloc int[SignatureLength];

        for (var y = 0; y < height; y++)
        {
            var row = frame.Slice(y * width, width);
            var blockY = Math.Min(Rows - 1, y * Rows / height);

            for (var x = 0; x < width; x++)
            {
                var blockX = Math.Min(Columns - 1, x * Columns / width);
                var blockIndex = (blockY * Columns) + blockX;
                totals[blockIndex] += row[x];
                counts[blockIndex]++;
            }
        }

        for (var index = 0; index < SignatureLength; index++)
        {
            signature[index] = counts[index] == 0
                ? (byte)0
                : (byte)(totals[index] / counts[index]);
        }
    }
}

internal sealed record FrameSample(int FrameIndex, double PtsSeconds, bool IsExactSignatureRepeat, double SignatureMeanAbsoluteDifference);

internal sealed record WindowMetric(double StartSeconds, double EndSeconds, double FramesPerSecond);

internal sealed record VisualUpdateStats(
    string Label,
    double AverageFps,
    double MinimumOneSecondFps,
    double RepeatFrameRatio,
    int UpdateCount,
    int LongestRepeatRunFrames,
    IReadOnlyList<WindowMetric> WorstWindows)
{
    public static VisualUpdateStats Build(
        IReadOnlyList<FrameSample> samples,
        Func<FrameSample, bool> isRepeat,
        string label)
    {
        if (samples.Count == 0)
        {
            return new VisualUpdateStats(label, 0.0, 0.0, 0.0, 0, 0, []);
        }

        var updateTimes = new List<double>(samples.Count) { samples[0].PtsSeconds };
        var longestRepeatRun = 0;
        var currentRepeatRun = 0;
        var repeatFrameCount = 0;

        for (var index = 1; index < samples.Count; index++)
        {
            if (isRepeat(samples[index]))
            {
                repeatFrameCount++;
                currentRepeatRun++;
                longestRepeatRun = Math.Max(longestRepeatRun, currentRepeatRun);
                continue;
            }

            currentRepeatRun = 0;
            updateTimes.Add(samples[index].PtsSeconds);
        }

        var duration = Math.Max(samples[^1].PtsSeconds - samples[0].PtsSeconds, 1.0 / 60.0);
        var averageFps = updateTimes.Count / duration;
        var windowMetrics = ComputeWindowMetrics(updateTimes, samples);
        var minimum = windowMetrics.Count == 0
            ? averageFps
            : windowMetrics.Min(window => window.FramesPerSecond);

        return new VisualUpdateStats(
            label,
            averageFps,
            minimum,
            repeatFrameCount / (double)Math.Max(1, samples.Count - 1),
            updateTimes.Count,
            longestRepeatRun,
            windowMetrics
                .OrderBy(window => window.FramesPerSecond)
                .ThenBy(window => window.StartSeconds)
                .Take(5)
                .ToArray());
    }

    public static VisualUpdateStats BuildMotionStutter(
        IReadOnlyList<FrameSample> samples,
        double nominalFrameRate,
        double motionThreshold,
        string label)
    {
        if (samples.Count == 0 || nominalFrameRate <= 0.0)
        {
            return new VisualUpdateStats(label, 0.0, 0.0, 0.0, 0, 0, []);
        }

        var dropFrames = new bool[samples.Count];
        var longestRepeatRun = 0;

        for (var index = 1; index < samples.Count; index++)
        {
            if (!samples[index].IsExactSignatureRepeat)
            {
                continue;
            }

            var runStart = index;
            while (index < samples.Count && samples[index].IsExactSignatureRepeat)
            {
                index++;
            }

            var runEndExclusive = index;
            var previousDiff = samples[runStart - 1].SignatureMeanAbsoluteDifference;
            var nextDiff = runEndExclusive < samples.Count
                ? samples[runEndExclusive].SignatureMeanAbsoluteDifference
                : 0.0;

            if (previousDiff >= motionThreshold && nextDiff >= motionThreshold)
            {
                for (var runIndex = runStart; runIndex < runEndExclusive; runIndex++)
                {
                    dropFrames[runIndex] = true;
                }

                longestRepeatRun = Math.Max(longestRepeatRun, runEndExclusive - runStart);
            }
        }

        var firstPts = samples[0].PtsSeconds;
        var lastPts = samples[^1].PtsSeconds;
        var duration = Math.Max(lastPts - firstPts, 1.0 / Math.Max(nominalFrameRate, 1.0));
        var totalDrops = dropFrames.Count(static dropped => dropped);
        var averageFps = Math.Max(0.0, nominalFrameRate - (totalDrops / duration));
        var windows = ComputeDropWindowMetrics(dropFrames, samples, nominalFrameRate);
        var minimum = windows.Count == 0
            ? averageFps
            : windows.Min(window => window.FramesPerSecond);

        return new VisualUpdateStats(
            label,
            averageFps,
            minimum,
            totalDrops / (double)Math.Max(1, samples.Count - 1),
            Math.Max(0, samples.Count - totalDrops),
            longestRepeatRun,
            windows
                .OrderBy(window => window.FramesPerSecond)
                .ThenBy(window => window.StartSeconds)
                .Take(5)
                .ToArray());
    }

    private static List<WindowMetric> ComputeWindowMetrics(IReadOnlyList<double> updateTimes, IReadOnlyList<FrameSample> samples)
    {
        var windows = new List<WindowMetric>();
        if (updateTimes.Count == 0)
        {
            return windows;
        }

        const double windowDurationSeconds = 1.0;
        const double windowStepSeconds = 0.1;
        var firstPts = samples[0].PtsSeconds;
        var lastPts = samples[^1].PtsSeconds;
        if (lastPts - firstPts < windowDurationSeconds)
        {
            return windows;
        }

        var updateStart = 0;
        var updateEnd = 0;
        for (var windowStart = firstPts; windowStart + windowDurationSeconds <= lastPts; windowStart += windowStepSeconds)
        {
            var windowEnd = windowStart + windowDurationSeconds;

            while (updateStart < updateTimes.Count && updateTimes[updateStart] < windowStart)
            {
                updateStart++;
            }

            if (updateEnd < updateStart)
            {
                updateEnd = updateStart;
            }

            while (updateEnd < updateTimes.Count && updateTimes[updateEnd] < windowEnd)
            {
                updateEnd++;
            }

            windows.Add(new WindowMetric(windowStart, windowEnd, (updateEnd - updateStart) / windowDurationSeconds));
        }

        return windows;
    }

    private static List<WindowMetric> ComputeDropWindowMetrics(bool[] dropFrames, IReadOnlyList<FrameSample> samples, double nominalFrameRate)
    {
        var windows = new List<WindowMetric>();
        if (samples.Count == 0 || nominalFrameRate <= 0.0)
        {
            return windows;
        }

        const double windowDurationSeconds = 1.0;
        const double windowStepSeconds = 0.1;
        var firstPts = samples[0].PtsSeconds;
        var lastPts = samples[^1].PtsSeconds;
        if (lastPts - firstPts < windowDurationSeconds)
        {
            return windows;
        }

        var startIndex = 0;
        var endIndex = 0;
        var dropCount = 0;

        for (var windowStart = firstPts; windowStart + windowDurationSeconds <= lastPts; windowStart += windowStepSeconds)
        {
            var windowEnd = windowStart + windowDurationSeconds;

            while (startIndex < samples.Count && samples[startIndex].PtsSeconds < windowStart)
            {
                if (dropFrames[startIndex])
                {
                    dropCount--;
                }

                startIndex++;
            }

            if (endIndex < startIndex)
            {
                endIndex = startIndex;
            }

            while (endIndex < samples.Count && samples[endIndex].PtsSeconds < windowEnd)
            {
                if (dropFrames[endIndex])
                {
                    dropCount++;
                }

                endIndex++;
            }

            windows.Add(new WindowMetric(
                windowStart,
                windowEnd,
                Math.Max(0.0, nominalFrameRate - (dropCount / windowDurationSeconds))));
        }

        return windows;
    }
}

internal static class OtsuThreshold
{
    public static double Compute(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        var maxValue = values.Max();
        if (maxValue <= 0.0)
        {
            return 0.0;
        }

        const int bins = 512;
        var histogram = new int[bins];
        foreach (var value in values)
        {
            var bin = (int)Math.Round((value / maxValue) * (bins - 1));
            histogram[Math.Clamp(bin, 0, bins - 1)]++;
        }

        var totalCount = values.Count;
        double totalWeighted = 0.0;
        for (var index = 0; index < bins; index++)
        {
            totalWeighted += index * histogram[index];
        }

        var backgroundWeight = 0;
        double backgroundWeighted = 0.0;
        var bestVariance = double.MinValue;
        var bestThreshold = 0;

        for (var index = 0; index < bins; index++)
        {
            backgroundWeight += histogram[index];
            if (backgroundWeight == 0)
            {
                continue;
            }

            var foregroundWeight = totalCount - backgroundWeight;
            if (foregroundWeight == 0)
            {
                break;
            }

            backgroundWeighted += index * histogram[index];
            var backgroundMean = backgroundWeighted / backgroundWeight;
            var foregroundMean = (totalWeighted - backgroundWeighted) / foregroundWeight;
            var meanDifference = backgroundMean - foregroundMean;
            var variance = backgroundWeight * foregroundWeight * meanDifference * meanDifference;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestThreshold = index;
            }
        }

        return maxValue * (bestThreshold / (double)(bins - 1));
    }
}

internal static class Quantiles
{
    public static double Compute(IReadOnlyList<double> sortedValues, double fraction)
    {
        if (sortedValues.Count == 0)
        {
            return 0.0;
        }

        var ordered = sortedValues.OrderBy(static value => value).ToArray();
        var index = (int)Math.Round((ordered.Length - 1) * Math.Clamp(fraction, 0.0, 1.0));
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }
}
