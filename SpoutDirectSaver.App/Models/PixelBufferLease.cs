using System;
using System.Buffers;
using System.Threading;

namespace SpoutDirectSaver.App.Models;

internal sealed class PixelBufferLease : IDisposable
{
    private byte[]? _buffer;
    private int _referenceCount;

    private PixelBufferLease(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
        _referenceCount = 1;
    }

    public byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(PixelBufferLease));

    public int Length { get; }

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public static PixelBufferLease Rent(int length)
    {
        return new PixelBufferLease(ArrayPool<byte>.Shared.Rent(length), length);
    }

    public void Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0 || _buffer is null)
            {
                throw new ObjectDisposedException(nameof(PixelBufferLease));
            }

            if (Interlocked.CompareExchange(ref _referenceCount, current + 1, current) == current)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _referenceCount, current - 1, current) != current)
            {
                continue;
            }

            if (current == 1)
            {
                var buffer = Interlocked.Exchange(ref _buffer, null);
                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            return;
        }
    }
}
