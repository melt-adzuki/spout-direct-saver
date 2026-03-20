using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spout.Interop;
using SpoutDirectSaver.App.Models;
using SpoutDirectSaver.App.Services;

var options = E2eOptions.Parse(args);
var outputDirectory = Path.Combine(Environment.CurrentDirectory, ".tmp-e2e-output");
Directory.CreateDirectory(outputDirectory);
var outputPath = Path.Combine(outputDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.mov");

Console.WriteLine($"sender={options.SenderName}");
Console.WriteLine($"captureSeconds={options.CaptureSeconds}");
Console.WriteLine($"output={outputPath}");

Process? senderProcess = null;

try
{
    senderProcess = LaunchTestSenderIfRequested(options);

var receiveOnly = MeasureReceiveOnly(options);
Console.WriteLine($"receive_only_frames={receiveOnly.FrameCount}");
Console.WriteLine($"receive_only_unique_frames={receiveOnly.UniqueFrameCount}");
Console.WriteLine($"receive_only_elapsed={receiveOnly.Elapsed.TotalSeconds:0.000}");
Console.WriteLine($"receive_only_fps={receiveOnly.FrameCount / receiveOnly.Elapsed.TotalSeconds:0.00}");
Console.WriteLine($"receive_only_unique_fps={receiveOnly.UniqueFrameCount / receiveOnly.Elapsed.TotalSeconds:0.00}");
Console.WriteLine($"receive_only_unique_avg_fps={receiveOnly.FrameRateStats.AverageFps:0.00}");
Console.WriteLine($"receive_only_unique_min_1s_fps={receiveOnly.FrameRateStats.MinimumOneSecondFps:0.00}");
Console.WriteLine($"receive_only_size={receiveOnly.Width}x{receiveOnly.Height}");
Console.WriteLine($"receive_only_sender_frame_delta={receiveOnly.SenderFrameDelta}");

    var recordResult = await RecordAndExportAsync(options, outputPath);
Console.WriteLine($"record_frames_seen={recordResult.TotalFramesSeen}");
Console.WriteLine($"record_unique_frames={recordResult.UniqueFramesSeen}");
Console.WriteLine($"record_elapsed={recordResult.CaptureElapsed.TotalSeconds:0.000}");
Console.WriteLine($"record_capture_fps={recordResult.TotalFramesSeen / recordResult.CaptureElapsed.TotalSeconds:0.00}");
Console.WriteLine($"record_unique_capture_fps={recordResult.UniqueFramesSeen / recordResult.CaptureElapsed.TotalSeconds:0.00}");
Console.WriteLine($"record_unique_avg_fps={recordResult.FrameRateStats.AverageFps:0.00}");
Console.WriteLine($"record_unique_min_1s_fps={recordResult.FrameRateStats.MinimumOneSecondFps:0.00}");
Console.WriteLine($"record_output={recordResult.OutputPath}");

    await ProbeVideoAsync(recordResult.OutputPath);
}
finally
{
    if (senderProcess is not null && !senderProcess.HasExited)
    {
        senderProcess.Kill(entireProcessTree: true);
        await senderProcess.WaitForExitAsync();
    }
}

static (int FrameCount, int UniqueFrameCount, int SenderFrameDelta, TimeSpan Elapsed, uint Width, uint Height, FrameRateStats FrameRateStats) MeasureReceiveOnly(E2eOptions options)
{
    using var receiver = new SpoutReceiver();
    receiver.CPUmode = options.CpuMode;
    receiver.BufferMode = options.BufferMode;
    receiver.SetFrameCount(options.UseFrameCount);
    receiver.Buffers = options.BufferCount;
    if (options.FrameSync)
    {
        receiver.SetFrameSync(options.SenderName);
    }

    if (!receiver.CreateOpenGL())
    {
        throw new InvalidOperationException("CreateOpenGL failed.");
    }

    receiver.SetReceiverName(options.SenderName);

    var firstFrameSeen = false;
    var frameCount = 0;
    var uniqueFrameCount = 0;
    var width = 0u;
    var height = 0u;
    var senderFrameStart = -1;
    var senderFrameEnd = -1;
    var lastObservedSenderFrame = -1;
    var lastAcceptedSenderFrame = -1;
    ulong? lastFallbackFingerprint = null;
    var duration = TimeSpan.FromSeconds(options.CaptureSeconds);
    var started = Stopwatch.StartNew();
    var endTicks = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
    var nextPollTicks = Stopwatch.GetTimestamp();
    var bufferLength = 0;
    var buffer = IntPtr.Zero;
    var acceptedFrameTicks = new List<long>();
    long? firstAcceptedFrameTick = null;

    try
    {
        while (Stopwatch.GetTimestamp() < endTicks)
        {
            if (!PrepareReceive(receiver, options.ReceiveMode) && !receiver.IsConnected)
            {
                Thread.Sleep(1);
                continue;
            }

            width = receiver.SenderWidth;
            height = receiver.SenderHeight;
            if (width == 0 || height == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            var requiredLength = checked((int)(width * height * 4));
            if (requiredLength != bufferLength)
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                buffer = Marshal.AllocHGlobal(requiredLength);
                bufferLength = requiredLength;
            }

            if (!options.IgnoreIsFrameNew && !receiver.IsFrameNew)
            {
                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                continue;
            }

            unsafe
            {
                var receiveWidth = width;
                var receiveHeight = height;
                var senderNameBytes = Encoding.ASCII.GetBytes($"{options.SenderName}\0");
                fixed (byte* senderNamePtr = senderNameBytes)
                {
                    var received = receiver.ReceiveImage(
                        (sbyte*)senderNamePtr,
                        ref receiveWidth,
                        ref receiveHeight,
                        (byte*)buffer,
                        0x80E1u,
                        true,
                        0);

                    if (!received)
                    {
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                        continue;
                    }
                }
            }

            var senderFrame = receiver.SenderFrame;
            if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
            {
                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                continue;
            }

            frameCount++;
            senderFrameStart = senderFrameStart < 0 ? senderFrame : senderFrameStart;
            senderFrameEnd = senderFrame;
            var acceptedFrameTick = Stopwatch.GetTimestamp();
            acceptedFrameTicks.Add(acceptedFrameTick);
            firstAcceptedFrameTick ??= acceptedFrameTick;
            if (senderFrame > 0)
            {
                if (senderFrame != lastObservedSenderFrame)
                {
                    uniqueFrameCount++;
                    lastObservedSenderFrame = senderFrame;
                }

                lastAcceptedSenderFrame = senderFrame;
            }
            else
            {
                var managedCopy = GC.AllocateUninitializedArray<byte>(bufferLength);
                Marshal.Copy(buffer, managedCopy, 0, bufferLength);
                var fingerprint = ComputeFingerprint(managedCopy);
                if (lastFallbackFingerprint != fingerprint)
                {
                    uniqueFrameCount++;
                    lastFallbackFingerprint = fingerprint;
                }
            }
            firstFrameSeen = true;
            WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
        }
    }
    finally
    {
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
        }

        receiver.ReleaseReceiver();
        receiver.CloseOpenGL();
    }

    if (!firstFrameSeen)
    {
        throw new InvalidOperationException("No frames were received.");
    }

    var captureStartedTicks = firstAcceptedFrameTick ?? Stopwatch.GetTimestamp();
    var captureEndedTicks = Stopwatch.GetTimestamp();
    return (
        frameCount,
        uniqueFrameCount,
        Math.Max(senderFrameEnd - senderFrameStart, 0),
        started.Elapsed,
        width,
        height,
        BuildFrameRateStats(acceptedFrameTicks, captureStartedTicks, captureEndedTicks));
}

static async Task<(int TotalFramesSeen, int UniqueFramesSeen, TimeSpan CaptureElapsed, string OutputPath, FrameRateStats FrameRateStats)> RecordAndExportAsync(E2eOptions options, string outputPath)
{
    using var receiver = new SpoutReceiver();
    receiver.CPUmode = options.CpuMode;
    receiver.BufferMode = options.BufferMode;
    receiver.SetFrameCount(options.UseFrameCount);
    receiver.Buffers = options.BufferCount;
    if (options.FrameSync)
    {
        receiver.SetFrameSync(options.SenderName);
    }

    if (!receiver.CreateOpenGL())
    {
        throw new InvalidOperationException("CreateOpenGL failed.");
    }

    receiver.SetReceiverName(options.SenderName);

    RecordingSession? session = null;
    IntPtr buffer = IntPtr.Zero;
    var bufferLength = 0;
    var frameCount = 0;
    var uniqueFrameCount = 0;
    var lastObservedSenderFrame = -1;
    ulong? lastFallbackFingerprint = null;
    var started = Stopwatch.StartNew();
    var endTicks = Stopwatch.GetTimestamp() + (long)(TimeSpan.FromSeconds(options.CaptureSeconds).TotalSeconds * Stopwatch.Frequency);
    var nextPollTicks = Stopwatch.GetTimestamp();
    var lastAcceptedSenderFrame = -1;
    var acceptedFrameTicks = new List<long>();
    long? firstAcceptedFrameTick = null;

    try
    {
        while (Stopwatch.GetTimestamp() < endTicks)
        {
            if (!PrepareReceive(receiver, options.ReceiveMode) && !receiver.IsConnected)
            {
                Thread.Sleep(1);
                continue;
            }

            var width = receiver.SenderWidth;
            var height = receiver.SenderHeight;
            if (width == 0 || height == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            var requiredLength = checked((int)(width * height * 4));
            if (requiredLength != bufferLength)
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }

                buffer = Marshal.AllocHGlobal(requiredLength);
                bufferLength = requiredLength;
            }

            if (!options.IgnoreIsFrameNew && !receiver.IsFrameNew)
            {
                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                continue;
            }

            unsafe
            {
                var receiveWidth = width;
                var receiveHeight = height;
                var senderNameBytes = Encoding.ASCII.GetBytes($"{options.SenderName}\0");
                fixed (byte* senderNamePtr = senderNameBytes)
                {
                    var received = receiver.ReceiveImage(
                        (sbyte*)senderNamePtr,
                        ref receiveWidth,
                        ref receiveHeight,
                        (byte*)buffer,
                        0x80E1u,
                        true,
                        0);

                    if (!received)
                    {
                        WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                        continue;
                    }
                }
            }

            var senderFrame = receiver.SenderFrame;
            if (senderFrame > 0 && senderFrame == lastAcceptedSenderFrame)
            {
                WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
                continue;
            }

            var managedCopy = GC.AllocateUninitializedArray<byte>(bufferLength);
            Marshal.Copy(buffer, managedCopy, 0, bufferLength);

            session ??= new RecordingSession(
                EncoderOption.CreateDefaults()[0],
                outputPath);
            var acceptedFrameTick = Stopwatch.GetTimestamp();
            session.AppendFrame(new FramePacket(
                managedCopy,
                width,
                height,
                options.SenderName,
                receiver.SenderFps,
                acceptedFrameTick,
                DateTimeOffset.UtcNow));
            frameCount++;
            acceptedFrameTicks.Add(acceptedFrameTick);
            firstAcceptedFrameTick ??= acceptedFrameTick;
            if (senderFrame > 0)
            {
                if (senderFrame != lastObservedSenderFrame)
                {
                    uniqueFrameCount++;
                    lastObservedSenderFrame = senderFrame;
                }

                lastAcceptedSenderFrame = senderFrame;
            }
            else
            {
                var fingerprint = ComputeFingerprint(managedCopy);
                if (lastFallbackFingerprint != fingerprint)
                {
                    uniqueFrameCount++;
                    lastFallbackFingerprint = fingerprint;
                }
            }
            WaitUntilNextPoll(ref nextPollTicks, receiver.SenderFps);
        }

        if (session is null)
        {
            throw new InvalidOperationException("Recording session was never started because no frames were received.");
        }

        var exporter = new VideoExportService();
        var captureElapsed = started.Elapsed;
        var captureStartedTicks = firstAcceptedFrameTick ?? Stopwatch.GetTimestamp();
        var captureEndedTicks = Stopwatch.GetTimestamp();
        var savedPath = await session.FinalizeAsync(exporter, CancellationToken.None);
        session = null;
        return (
            frameCount,
            uniqueFrameCount,
            captureElapsed,
            savedPath,
            BuildFrameRateStats(acceptedFrameTicks, captureStartedTicks, captureEndedTicks));
    }
    finally
    {
        if (session is not null)
        {
            await session.DisposeAsync();
        }

        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
        }

        receiver.ReleaseReceiver();
        receiver.CloseOpenGL();
    }
}

static async Task ProbeVideoAsync(string outputPath)
{
    var psi = new ProcessStartInfo
    {
        FileName = "ffprobe",
        Arguments = $"-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate,nb_frames,pix_fmt,width,height -show_entries format=duration -of default=noprint_wrappers=1:nokey=0 \"{outputPath}\"",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi)!;
    var stdout = await process.StandardOutput.ReadToEndAsync();
    var stderr = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    Console.WriteLine("ffprobe_output_begin");
    Console.WriteLine(stdout.Trim());
    if (!string.IsNullOrWhiteSpace(stderr))
    {
        Console.WriteLine(stderr.Trim());
    }

    Console.WriteLine("ffprobe_output_end");
}

static void WaitUntilNextPoll(ref long nextPollTicks, double senderFps)
{
    var targetFps = senderFps > 1.0 ? Math.Min(senderFps, 120.0) : 120.0;
    nextPollTicks += (long)Math.Round(Stopwatch.Frequency / targetFps);

    while (Stopwatch.GetTimestamp() < nextPollTicks)
    {
        Thread.SpinWait(64);
    }
}

static bool PrepareReceive(SpoutReceiver receiver, ReceiveMode receiveMode)
{
    return receiveMode switch
    {
        ReceiveMode.ImageOnly => receiver.IsConnected ? true : receiver.ReceiveTexture(),
        _ => receiver.ReceiveTexture()
    };
}

static ulong ComputeFingerprint(ReadOnlySpan<byte> pixelData)
{
    const ulong offsetBasis = 14695981039346656037UL;
    const ulong prime = 1099511628211UL;

    var hash = offsetBasis ^ (ulong)pixelData.Length;
    if (pixelData.IsEmpty)
    {
        return hash;
    }

    var checkpoints = 8;
    for (var i = 0; i < checkpoints; i++)
    {
        var offset = (int)(((long)pixelData.Length - 1) * i / Math.Max(checkpoints - 1, 1));
        hash ^= pixelData[offset];
        hash *= prime;
    }

    return hash;
}

static FrameRateStats BuildFrameRateStats(IReadOnlyList<long> acceptedFrameTicks, long captureStartedTicks, long captureEndedTicks)
{
    var elapsedTicks = Math.Max(captureEndedTicks - captureStartedTicks, 1);
    var averageFps = acceptedFrameTicks.Count * Stopwatch.Frequency / (double)elapsedTicks;
    if (acceptedFrameTicks.Count == 0)
    {
        return new FrameRateStats(0.0, 0.0);
    }

    var windowTicks = Stopwatch.Frequency;
    var windowStart = captureStartedTicks;
    var minimumFps = double.MaxValue;
    var startIndex = 0;
    var endIndex = 0;

    while (windowStart < captureEndedTicks)
    {
        var windowEnd = Math.Min(windowStart + windowTicks, captureEndedTicks);
        while (startIndex < acceptedFrameTicks.Count && acceptedFrameTicks[startIndex] < windowStart)
        {
            startIndex++;
        }

        while (endIndex < acceptedFrameTicks.Count && acceptedFrameTicks[endIndex] < windowEnd)
        {
            endIndex++;
        }

        var seconds = Math.Max((windowEnd - windowStart) / (double)Stopwatch.Frequency, 1.0 / Stopwatch.Frequency);
        var fps = (endIndex - startIndex) / seconds;
        minimumFps = Math.Min(minimumFps, fps);
        windowStart = windowEnd;
    }

    return new FrameRateStats(averageFps, minimumFps == double.MaxValue ? averageFps : minimumFps);
}

static Process? LaunchTestSenderIfRequested(E2eOptions options)
{
    if (!options.LaunchTestSender)
    {
        return null;
    }

    var senderProjectPath = Path.Combine(
        Environment.CurrentDirectory,
        "SpoutDirectSaver.TestSender",
        "SpoutDirectSaver.TestSender.csproj");
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments =
            $"run --project \"{senderProjectPath}\" -- --name \"{options.SenderName}\" --width {options.Width} --height {options.Height} --fps {options.FrameRate:0.###} --seconds {options.CaptureSeconds + 3}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start test sender.");
    _ = Task.Run(async () =>
    {
        var stdout = await process.StandardOutput.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine("test_sender_stdout_begin");
            Console.WriteLine(stdout.Trim());
            Console.WriteLine("test_sender_stdout_end");
        }
    });
    _ = Task.Run(async () =>
    {
        var stderr = await process.StandardError.ReadToEndAsync();
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.WriteLine("test_sender_stderr_begin");
            Console.WriteLine(stderr.Trim());
            Console.WriteLine("test_sender_stderr_end");
        }
    });

    Thread.Sleep(1500);
    return process;
}

internal sealed record E2eOptions(
    string SenderName,
    int CaptureSeconds,
    bool LaunchTestSender,
    bool IgnoreIsFrameNew,
    uint Width,
    uint Height,
    double FrameRate,
    ReceiveMode ReceiveMode,
    bool CpuMode,
    bool BufferMode,
    bool UseFrameCount,
    int BufferCount,
    bool FrameSync)
{
    public static E2eOptions Parse(string[] args)
    {
        var senderName = "VRCSender1";
        var captureSeconds = 5;
        var launchTestSender = false;
        var ignoreIsFrameNew = false;
        var width = 3840u;
        var height = 2160u;
        var frameRate = 60.0;
        var receiveMode = ReceiveMode.TextureThenImage;
        var cpuMode = true;
        var bufferMode = true;
        var useFrameCount = true;
        var bufferCount = 2;
        var frameSync = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--sender":
                    senderName = args[++index];
                    break;
                case "--seconds":
                    captureSeconds = int.Parse(args[++index]);
                    break;
                case "--launch-test-sender":
                    launchTestSender = true;
                    ignoreIsFrameNew = true;
                    break;
                case "--ignore-is-frame-new":
                    ignoreIsFrameNew = true;
                    break;
                case "--width":
                    width = uint.Parse(args[++index]);
                    break;
                case "--height":
                    height = uint.Parse(args[++index]);
                    break;
                case "--fps":
                    frameRate = double.Parse(args[++index]);
                    break;
                case "--image-only":
                    receiveMode = ReceiveMode.ImageOnly;
                    break;
                case "--no-cpu-mode":
                    cpuMode = false;
                    break;
                case "--no-buffer-mode":
                    bufferMode = false;
                    break;
                case "--no-frame-count":
                    useFrameCount = false;
                    break;
                case "--buffers":
                    bufferCount = int.Parse(args[++index]);
                    break;
                case "--frame-sync":
                    frameSync = true;
                    break;
                default:
                    if (!args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        senderName = args[index];
                    }
                    break;
            }
        }

        return new E2eOptions(senderName, captureSeconds, launchTestSender, ignoreIsFrameNew, width, height, frameRate, receiveMode, cpuMode, bufferMode, useFrameCount, bufferCount, frameSync);
    }
}

internal enum ReceiveMode
{
    TextureThenImage,
    ImageOnly
}

internal sealed record FrameRateStats(double AverageFps, double MinimumOneSecondFps);
