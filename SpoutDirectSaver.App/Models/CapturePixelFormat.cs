namespace SpoutDirectSaver.App.Models;

internal enum CapturePixelFormat
{
    Bgra32,
    Rgba32
}

internal static class CapturePixelFormatExtensions
{
    public static string ToFfmpegPixelFormat(this CapturePixelFormat pixelFormat)
    {
        return pixelFormat switch
        {
            CapturePixelFormat.Rgba32 => "rgba",
            _ => "bgra"
        };
    }
}
