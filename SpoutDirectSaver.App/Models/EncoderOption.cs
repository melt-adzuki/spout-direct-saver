using System;

namespace SpoutDirectSaver.App.Models;

internal enum EncoderProfileKind
{
    HevcNvencPackedAlphaMkv,
    HevcNvencFfv1AlphaMkv,
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

    public bool RequiresRealtimeEncoding => false;

    public bool UsesRealtimeRgbIntermediate => Kind == EncoderProfileKind.HevcNvencFfv1AlphaMkv;

    public bool UsesRealtimePackedIntermediate => Kind == EncoderProfileKind.HevcNvencPackedAlphaMkv;

    public bool UsesAnyRealtimeIntermediate => UsesRealtimeRgbIntermediate || UsesRealtimePackedIntermediate;

    public string BuildArguments(uint width, uint height, double frameRate, CapturePixelFormat pixelFormat, string outputPath)
        => BuildArgumentsCore(width, height, frameRate, pixelFormat, "-i -", outputPath);

    public string BuildPipeArguments(uint width, uint height, double frameRate, CapturePixelFormat pixelFormat, string inputPath, string outputPath)
        => BuildArgumentsCore(width, height, frameRate, pixelFormat, $"-blocksize 33554432 -i {Quote(inputPath)}", outputPath);

    private string BuildArgumentsCore(uint width, uint height, double frameRate, CapturePixelFormat pixelFormat, string inputSpecifier, string outputPath)
    {
        var gop = Math.Max(1, (int)Math.Round(frameRate));
        var rawInput = $"-f rawvideo -pixel_format {pixelFormat.ToFfmpegPixelFormat()} -video_size {width}x{height} -framerate {frameRate:0.###} {inputSpecifier}";

        return Kind switch
        {
            EncoderProfileKind.HevcNvencPackedAlphaMkv =>
                $"-y {rawInput} -an -c:v hevc_nvenc -preset:v p1 -tune:v ll -rc:v vbr -cq:v 21 -b:v 0 -g:v {gop} -pix_fmt yuv420p -profile:v main -cues_to_front 1 {Quote(outputPath)}",
            EncoderProfileKind.HevcNvencFfv1AlphaMkv =>
                $"-y {rawInput} -an -filter_complex \"[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]\" -map \"[rgb]\" -c:v:0 hevc_nvenc -preset:v:0 p1 -tune:v:0 ll -rc:v:0 vbr -cq:v:0 21 -b:v:0 0 -g:v:0 {gop} -pix_fmt:v:0 yuv420p -profile:v:0 main -map \"[aout]\" -c:v:1 ffv1 -level:v:1 3 -coder:v:1 1 -context:v:1 1 -g:v:1 1 -slicecrc:v:1 1 -pix_fmt:v:1 gray -cues_to_front 1 {Quote(outputPath)}",
            EncoderProfileKind.PngMov =>
                $"-y {rawInput} -an -c:v png -pred mixed -pix_fmt rgba -movflags +faststart -video_track_timescale 120000 {Quote(outputPath)}",
            EncoderProfileKind.Ffv1Mkv =>
                $"-y {rawInput} -an -c:v ffv1 -level 3 -coder 1 -context 1 -g 1 -slicecrc 1 -pix_fmt bgra -cues_to_front 1 {Quote(outputPath)}",
            _ => throw new InvalidOperationException($"Unknown encoder kind: {Kind}")
        };
    }

    public override string ToString() => DisplayName;

    public static EncoderOption[] CreateDefaults()
    {
        return
        [
            new(
                EncoderProfileKind.HevcNvencFfv1AlphaMkv,
                "HEVC NVENC / MKV + FFV1 alpha sidecar",
                ".mkv",
                "録画中は RGB 本体を HEVC NVENC(yuv420p) でリアルタイム圧縮し、alpha は grayscale FFV1 の sidecar 動画として別保存します。再生互換性と temp 帯域の両立を優先した構成です。",
                "Matroska MKV (*.mkv)|*.mkv"),
            new(
                EncoderProfileKind.HevcNvencPackedAlphaMkv,
                "HEVC NVENC / MKV (RGB+Alpha packed)",
                ".mkv",
                "RGB を左半分、alpha グレースケールを右半分に横並びパックして、1 本の HEVC NVENC 動画へリアルタイム圧縮します。preview は packed 表示になりますが、alpha sidecar を使わない分、比較検証向けの試験構成です。",
                "Matroska MKV (*.mkv)|*.mkv"),
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
