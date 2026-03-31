using System;

namespace SpoutDirectSaver.App.Models;

internal enum EncoderProfileKind
{
    HevcNvencMp4AlphaMp4,
    PngMov
}

internal sealed class EncoderOption
{
    public EncoderOption(
        EncoderProfileKind kind,
        string displayName,
        string extension,
        string description,
        string fileDialogFilter)
    {
        Kind = kind;
        DisplayName = displayName;
        Extension = extension;
        Description = description;
        FileDialogFilter = fileDialogFilter;
    }

    public EncoderProfileKind Kind { get; }

    public string DisplayName { get; }

    public string Extension { get; }

    public string Description { get; }

    public string FileDialogFilter { get; }

    public bool RequiresRealtimeEncoding => false;

    public bool UsesRealtimeRgbIntermediate => Kind == EncoderProfileKind.HevcNvencMp4AlphaMp4;

    public string BuildArguments(uint width, uint height, double frameRate, string outputPath)
        => BuildArgumentsCore(width, height, frameRate, "-i -", outputPath);

    public string BuildPipeArguments(uint width, uint height, double frameRate, string inputPath, string outputPath)
        => BuildArgumentsCore(width, height, frameRate, $"-blocksize 33554432 -i {Quote(inputPath)}", outputPath);

    private string BuildArgumentsCore(uint width, uint height, double frameRate, string inputSpecifier, string outputPath)
    {
        var rawInput = $"-f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {frameRate:0.###} {inputSpecifier}";

        return Kind switch
        {
            EncoderProfileKind.PngMov =>
                $"-y {rawInput} -an -c:v png -pred mixed -pix_fmt rgba -movflags +faststart -video_track_timescale 120000 {Quote(outputPath)}",
            _ => throw new InvalidOperationException($"Encoder kind {Kind} does not use direct ffmpeg export.")
        };
    }

    public override string ToString() => DisplayName;

    public static EncoderOption[] CreateDefaults()
    {
        return
        [
            new(
                EncoderProfileKind.HevcNvencMp4AlphaMp4,
                "HEVC NVENC / MP4 + HEVC alpha sidecar",
                ".mp4",
                "録画中は RGB 本体を HEVC NVENC でリアルタイム圧縮し、alpha は HEVC/MP4 の sidecar 動画として別保存します。再生互換性と一時ファイル帯域の両立を優先した構成です。",
                "MP4 (*.mp4)|*.mp4"),
            new(
                EncoderProfileKind.PngMov,
                "PNG / MOV (lossless RGBA 8bit, alpha保持)",
                ".mov",
                "PNG エンコーダーは rgba を直接扱えるので、最終動画でも RGBA 8bit を維持しやすい構成です。ファイルサイズは大きめです。",
                "QuickTime MOV (*.mov)|*.mov")
        ];
    }

    private static string Quote(string path)
    {
        return $"\"{path.Replace("\"", "\\\"")}\"";
    }
}
