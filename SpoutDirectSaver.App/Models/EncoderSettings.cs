using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Vortice.MediaFoundation;

namespace SpoutDirectSaver.App.Models;

internal sealed class EncoderSettingsRoot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public RgbMediaFoundationEncoderSettings Rgb { get; set; } = new();

    public AlphaNvencEncoderSettings Alpha { get; set; } = new();

    public static EncoderSettingsRoot CreateDefaults() => new();

    public EncoderSettingsRoot Clone()
    {
        return new EncoderSettingsRoot
        {
            SchemaVersion = SchemaVersion,
            Rgb = Rgb.Clone(),
            Alpha = Alpha.Clone()
        };
    }

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Rgb.Normalize();
        Alpha.Normalize();
    }
}

internal sealed class RgbMediaFoundationEncoderSettings
{
    public RgbMediaFoundationRateControlMode RateControlMode { get; set; } = RgbMediaFoundationRateControlMode.Auto;

    public int TargetBitrateMbps { get; set; } = 40;

    public int BufferSizeMb { get; set; } = 0;

    public int QualityVsSpeed { get; set; } = 0;

    public bool LowLatency { get; set; } = true;

    public bool UseConstantQp { get; set; } = false;

    public int ConstantQp { get; set; } = 23;

    public int MinQp { get; set; } = -1;

    public int MaxQp { get; set; } = -1;

    public int GopSize { get; set; } = 0;

    public RgbMediaFoundationContentTypeHint ContentTypeHint { get; set; } = RgbMediaFoundationContentTypeHint.Auto;

    public int WorkerThreads { get; set; } = 0;

    public RgbMediaFoundationEncoderSettings Clone()
    {
        return (RgbMediaFoundationEncoderSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        TargetBitrateMbps = Clamp(TargetBitrateMbps, 0, 1_000_000);
        BufferSizeMb = Clamp(BufferSizeMb, 0, 1_000_000);
        QualityVsSpeed = Clamp(QualityVsSpeed, 0, 100);
        ConstantQp = Clamp(ConstantQp, 0, 51);
        MinQp = Clamp(MinQp, -1, 51);
        MaxQp = Clamp(MaxQp, -1, 51);
        GopSize = Clamp(GopSize, 0, 1_000_000);
        WorkerThreads = Clamp(WorkerThreads, 0, 256);
    }

    public void ApplyTo(IMFAttributes encodingParameters)
    {
        if (RateControlMode != RgbMediaFoundationRateControlMode.Auto)
        {
            encodingParameters.Set(
                CodecApiGuids.AvEncCommonRateControlMode,
                (uint)RateControlMode.ToCodecApiValue());
        }

        if (TargetBitrateMbps > 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncCommonMeanBitRate, checked((uint)(TargetBitrateMbps * 1_000_000)));
        }

        if (BufferSizeMb > 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncCommonBufferSize, checked((uint)(BufferSizeMb * 1_000_000)));
        }

        if (QualityVsSpeed > 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncCommonQualityVsSpeed, checked((uint)QualityVsSpeed));
        }

        encodingParameters.Set(CodecApiGuids.AvLowLatencyMode, LowLatency ? 1u : 0u);

        if (UseConstantQp)
        {
            encodingParameters.Set(CodecApiGuids.AvEncVideoEncodeQp, checked((uint)ConstantQp));
        }

        if (MinQp >= 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncVideoMinQp, checked((uint)MinQp));
        }

        if (MaxQp >= 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncVideoMaxQp, checked((uint)MaxQp));
        }

        if (GopSize > 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncMpvGopSize, checked((uint)GopSize));
        }

        if (ContentTypeHint != RgbMediaFoundationContentTypeHint.Auto)
        {
            encodingParameters.Set(
                CodecApiGuids.VideoEncoderDisplayContentType,
                checked((uint)ContentTypeHint.ToCodecApiValue()));
        }

        if (WorkerThreads > 0)
        {
            encodingParameters.Set(CodecApiGuids.AvEncNumWorkerThreads, checked((uint)WorkerThreads));
        }
    }

    private static int Clamp(int value, int min, int max)
        => Math.Clamp(value, min, max);
}

internal sealed class AlphaNvencEncoderSettings
{
    public AlphaNvencPreset Preset { get; set; } = AlphaNvencPreset.P3;

    public AlphaNvencTune Tune { get; set; } = AlphaNvencTune.Hq;

    public AlphaNvencRateControlMode RateControlMode { get; set; } = AlphaNvencRateControlMode.Vbr;

    public int TargetBitrateMbps { get; set; } = 0;

    public int ConstantQuality { get; set; } = 19;

    public int ConstantQp { get; set; } = 23;

    public int MinQp { get; set; } = -1;

    public int MaxQp { get; set; } = -1;

    public int LookaheadFrames { get; set; } = 0;

    public bool SpatialAq { get; set; } = false;

    public bool TemporalAq { get; set; } = false;

    public int AqStrength { get; set; } = 8;

    public bool ZeroLatency { get; set; } = false;

    public int BFrames { get; set; } = 0;

    public int GopSize { get; set; } = 0;

    public AlphaNvencProfile Profile { get; set; } = AlphaNvencProfile.Main;

    public AlphaNvencLevel Level { get; set; } = AlphaNvencLevel.Auto;

    public AlphaNvencEncoderSettings Clone()
    {
        return (AlphaNvencEncoderSettings)MemberwiseClone();
    }

    public void Normalize()
    {
        TargetBitrateMbps = Clamp(TargetBitrateMbps, 0, 1_000_000);
        ConstantQuality = Clamp(ConstantQuality, 0, 51);
        ConstantQp = Clamp(ConstantQp, 0, 51);
        MinQp = Clamp(MinQp, -1, 51);
        MaxQp = Clamp(MaxQp, -1, 51);
        LookaheadFrames = Clamp(LookaheadFrames, 0, 1_000_000);
        AqStrength = Clamp(AqStrength, 1, 15);
        BFrames = Clamp(BFrames, 0, 32);
        GopSize = Clamp(GopSize, 0, 1_000_000);
    }

    public string BuildArguments(uint width, uint height, double frameRate, string inputArguments, string outputPath)
    {
        var gop = GopSize > 0 ? GopSize : Math.Max(1, (int)Math.Round(frameRate));
        var args = new StringBuilder();
        Append(args, "-y");
        Append(args, "-f rawvideo");
        Append(args, "-pixel_format gray");
        Append(args, $"-video_size {width}x{height}");
        Append(args, $"-framerate {frameRate.ToString("0.###", CultureInfo.InvariantCulture)}");
        Append(args, inputArguments);
        Append(args, "-an");
        Append(args, "-vf format=yuv420p");
        Append(args, "-c:v hevc_nvenc");
        Append(args, $"-preset:v {Preset.ToFfmpegValue()}");
        Append(args, $"-tune:v {Tune.ToFfmpegValue()}");
        Append(args, $"-rc:v {RateControlMode.ToFfmpegValue()}");

        switch (RateControlMode)
        {
            case AlphaNvencRateControlMode.Vbr:
                Append(args, $"-cq:v {ConstantQuality}");
                if (TargetBitrateMbps > 0)
                {
                    Append(args, $"-b:v {FormatBitrate(TargetBitrateMbps)}");
                }
                break;
            case AlphaNvencRateControlMode.Cbr:
                if (TargetBitrateMbps > 0)
                {
                    Append(args, $"-b:v {FormatBitrate(TargetBitrateMbps)}");
                }
                break;
            case AlphaNvencRateControlMode.ConstQp:
                Append(args, $"-qp:v {ConstantQp}");
                break;
        }

        if (MinQp >= 0)
        {
            Append(args, $"-qmin:v {MinQp}");
        }

        if (MaxQp >= 0)
        {
            Append(args, $"-qmax:v {MaxQp}");
        }

        if (LookaheadFrames > 0)
        {
            Append(args, $"-rc-lookahead {LookaheadFrames}");
        }

        if (SpatialAq)
        {
            Append(args, "-spatial-aq 1");
        }

        if (TemporalAq)
        {
            Append(args, "-temporal-aq 1");
        }

        Append(args, $"-aq-strength {AqStrength}");
        if (ZeroLatency)
        {
            Append(args, "-zerolatency 1");
        }

        Append(args, $"-bf:v {BFrames}");
        Append(args, $"-g:v {gop}");
        Append(args, "-pix_fmt:v yuv420p");
        Append(args, $"-profile:v {Profile.ToFfmpegValue()}");
        if (Level != AlphaNvencLevel.Auto)
        {
            Append(args, $"-level:v {Level.ToFfmpegValue()}");
        }
        Append(args, "-movflags +faststart");
        Append(args, "-video_track_timescale 120000");
        Append(args, $"\"{Quote(outputPath)}\"");
        return args.ToString();
    }

    private static string FormatBitrate(int megabitsPerSecond)
    {
        if (megabitsPerSecond <= 0)
        {
            return "0";
        }

        return $"{megabitsPerSecond}M";
    }

    private static void Append(StringBuilder builder, string part)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(part);
    }

    private static string Quote(string value)
    {
        return value.Replace("\"", "\\\"");
    }

    private static int Clamp(int value, int min, int max)
        => Math.Clamp(value, min, max);
}

internal enum RgbMediaFoundationRateControlMode
{
    Auto = 0,
    Cbr = 1,
    Quality = 2
}

internal enum RgbMediaFoundationContentTypeHint
{
    Auto = 0,
    FullScreenVideo = 1
}

internal enum AlphaNvencRateControlMode
{
    Vbr = 0,
    Cbr = 1,
    ConstQp = 2
}

internal enum AlphaNvencTune
{
    Hq = 0,
    Ll = 1,
    Ull = 2,
    Lossless = 3,
    Uhq = 4
}

internal enum AlphaNvencProfile
{
    Main = 0,
    Main10 = 1,
    Rext = 2,
    Mv = 3
}

internal enum AlphaNvencLevel
{
    Auto = 0,
    L1 = 30,
    L1b = 9,
    L2 = 60,
    L2_1 = 63,
    L3 = 90,
    L3_1 = 93,
    L4 = 120,
    L4_1 = 123,
    L5 = 150,
    L5_1 = 153,
    L5_2 = 156,
    L6 = 180,
    L6_1 = 183,
    L6_2 = 186
}

internal enum AlphaNvencPreset
{
    Default = 0,
    Slow = 1,
    Medium = 2,
    Fast = 3,
    Hp = 4,
    Hq = 5,
    Bd = 6,
    Ll = 7,
    Llhq = 8,
    Llhp = 9,
    Lossless = 10,
    LosslessHp = 11,
    P1 = 12,
    P2 = 13,
    P3 = 14,
    P4 = 15,
    P5 = 16,
    P6 = 17,
    P7 = 18
}

internal static class RgbMediaFoundationRateControlModeExtensions
{
    public static uint ToCodecApiValue(this RgbMediaFoundationRateControlMode mode)
    {
        return mode switch
        {
            RgbMediaFoundationRateControlMode.Cbr => 0u,
            RgbMediaFoundationRateControlMode.Quality => 3u,
            _ => throw new InvalidOperationException($"Unsupported RGB rate control mode: {mode}")
        };
    }
}

internal static class RgbMediaFoundationContentTypeHintExtensions
{
    public static uint ToCodecApiValue(this RgbMediaFoundationContentTypeHint mode)
    {
        return mode switch
        {
            RgbMediaFoundationContentTypeHint.FullScreenVideo => 1u,
            _ => throw new InvalidOperationException($"Unsupported RGB content type hint: {mode}")
        };
    }
}

internal static class AlphaNvencValueExtensions
{
    public static string ToFfmpegValue(this AlphaNvencRateControlMode mode)
    {
        return mode switch
        {
            AlphaNvencRateControlMode.Vbr => "vbr",
            AlphaNvencRateControlMode.Cbr => "cbr",
            AlphaNvencRateControlMode.ConstQp => "constqp",
            _ => throw new InvalidOperationException($"Unsupported NVENC rate control mode: {mode}")
        };
    }

    public static string ToFfmpegValue(this AlphaNvencTune tune)
    {
        return tune switch
        {
            AlphaNvencTune.Hq => "hq",
            AlphaNvencTune.Ll => "ll",
            AlphaNvencTune.Ull => "ull",
            AlphaNvencTune.Lossless => "lossless",
            AlphaNvencTune.Uhq => "uhq",
            _ => throw new InvalidOperationException($"Unsupported NVENC tune: {tune}")
        };
    }

    public static string ToFfmpegValue(this AlphaNvencProfile profile)
    {
        return profile switch
        {
            AlphaNvencProfile.Main => "main",
            AlphaNvencProfile.Main10 => "main10",
            AlphaNvencProfile.Rext => "rext",
            AlphaNvencProfile.Mv => "mv",
            _ => throw new InvalidOperationException($"Unsupported NVENC profile: {profile}")
        };
    }

    public static string ToFfmpegValue(this AlphaNvencPreset preset)
    {
        return preset switch
        {
            AlphaNvencPreset.Default => "default",
            AlphaNvencPreset.Slow => "slow",
            AlphaNvencPreset.Medium => "medium",
            AlphaNvencPreset.Fast => "fast",
            AlphaNvencPreset.Hp => "hp",
            AlphaNvencPreset.Hq => "hq",
            AlphaNvencPreset.Bd => "bd",
            AlphaNvencPreset.Ll => "ll",
            AlphaNvencPreset.Llhq => "llhq",
            AlphaNvencPreset.Llhp => "llhp",
            AlphaNvencPreset.Lossless => "lossless",
            AlphaNvencPreset.LosslessHp => "losslesshp",
            AlphaNvencPreset.P1 => "p1",
            AlphaNvencPreset.P2 => "p2",
            AlphaNvencPreset.P3 => "p3",
            AlphaNvencPreset.P4 => "p4",
            AlphaNvencPreset.P5 => "p5",
            AlphaNvencPreset.P6 => "p6",
            AlphaNvencPreset.P7 => "p7",
            _ => throw new InvalidOperationException($"Unsupported NVENC preset: {preset}")
        };
    }

    public static string ToFfmpegValue(this AlphaNvencLevel level)
    {
        return level switch
        {
            AlphaNvencLevel.Auto => "auto",
            AlphaNvencLevel.L1 => "1",
            AlphaNvencLevel.L1b => "1b",
            AlphaNvencLevel.L2 => "2",
            AlphaNvencLevel.L2_1 => "2.1",
            AlphaNvencLevel.L3 => "3",
            AlphaNvencLevel.L3_1 => "3.1",
            AlphaNvencLevel.L4 => "4",
            AlphaNvencLevel.L4_1 => "4.1",
            AlphaNvencLevel.L5 => "5",
            AlphaNvencLevel.L5_1 => "5.1",
            AlphaNvencLevel.L5_2 => "5.2",
            AlphaNvencLevel.L6 => "6",
            AlphaNvencLevel.L6_1 => "6.1",
            AlphaNvencLevel.L6_2 => "6.2",
            _ => throw new InvalidOperationException($"Unsupported NVENC level: {level}")
        };
    }
}

internal static class CodecApiGuids
{
    public static readonly Guid AvEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AvEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AvEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid AvEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    public static readonly Guid AvLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AvEncVideoEncodeQp = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");
    public static readonly Guid AvEncVideoMinQp = new("0ee22c6a-a37c-4568-b5f1-9d4c2b3ab886");
    public static readonly Guid AvEncVideoMaxQp = new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2");
    public static readonly Guid VideoEncoderDisplayContentType = new("79b90b27-f4b1-42dc-9dd7-cdaf8135c400");
    public static readonly Guid AvEncNumWorkerThreads = new("b0c8bf60-16f7-4951-a30b-1db1609293d6");
    public static readonly Guid AvEncMpvGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
}
