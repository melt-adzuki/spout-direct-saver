using System;
using System.Buffers;

namespace SpoutDirectSaver.App.Models;

internal sealed class PixelBufferLease : IDisposable
{
    private byte[]? _buffer;

    private PixelBufferLease(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PixelBufferLease));

    public int Length { get; }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public static PixelBufferLease Rent(int length)
    {
        return new PixelBufferLease(ArrayPool<byte>.Shared.Rent(length), length);
    }

    public void Dispose()
    {
        if (_buffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
    }
}
