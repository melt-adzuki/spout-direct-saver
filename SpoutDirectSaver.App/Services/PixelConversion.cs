using System;

namespace SpoutDirectSaver.App.Services;

internal static class PixelConversion
{
    public static void ConvertRgbaToBgra(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        for (var index = 0; index < source.Length; index += 4)
        {
            destination[index] = source[index + 2];
            destination[index + 1] = source[index + 1];
            destination[index + 2] = source[index];
            destination[index + 3] = source[index + 3];
        }
    }
}
