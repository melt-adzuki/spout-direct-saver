using System;

namespace SpoutDirectSaver.App.Models;

internal enum EncoderProfileKind
{
    PngMov,
    Ffv1Mkv
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

    public string BuildArguments(uint width, uint height, double frameRate, string outputPath)
    {
        return Kind switch
        {
            EncoderProfileKind.PngMov =>
                $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {frameRate:0.###} -i - -an -c:v png -pred mixed -pix_fmt rgba -movflags +faststart -video_track_timescale 120000 {Quote(outputPath)}",
            EncoderProfileKind.Ffv1Mkv =>
                $"-y -f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {frameRate:0.###} -i - -an -c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slicecrc 1 -pix_fmt bgra -cues_to_front 1 {Quote(outputPath)}",
            _ => throw new InvalidOperationException($"Unknown encoder kind: {Kind}")
        };
    }

    public override string ToString() => DisplayName;

    public static EncoderOption[] CreateDefaults()
    {
        return
        [
            new(
                EncoderProfileKind.PngMov,
                "PNG / MOV (lossless RGBA 8bit, alpha保持)",
                ".mov",
                "PNG エンコーダーは rgba を直接扱えるので、最終動画でも RGBA 8bit を維持しやすい構成です。ファイルサイズは大きめです。",
                "QuickTime MOV (*.mov)|*.mov"),
            new(
                EncoderProfileKind.Ffv1Mkv,
                "FFV1 / MKV (lossless, alpha保持, 容量効率重視)",
                ".mkv",
                "FFV1 は alpha plane と BGRA 系ピクセルフォーマットを扱えるロスレスコーデックです。RGBA 受信を保ちつつ、保存時のみ BGRA に並び替えます。",
                "Matroska MKV (*.mkv)|*.mkv")
        ];
    }

    private static string Quote(string path)
    {
        return $"\"{path.Replace("\"", "\\\"")}\"";
    }
}
