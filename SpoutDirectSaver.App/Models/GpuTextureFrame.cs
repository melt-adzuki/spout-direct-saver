using System;
using System.Threading;
using Vortice.Direct3D11;

namespace SpoutDirectSaver.App.Models;

internal sealed class GpuTextureFrame : IDisposable
{
    private readonly Action? _release;
    private int _referenceCount = 1;

    public GpuTextureFrame(
        ID3D11Device device,
        ID3D11Texture2D texture,
        uint width,
        uint height,
        int adapterIndex,
        IntPtr sharedHandle,
        Action? release)
    {
        Device = device;
        Texture = texture;
        Width = width;
        Height = height;
        AdapterIndex = adapterIndex;
        SharedHandle = sharedHandle;
        _release = release;
    }

    public ID3D11Device Device { get; }

    public ID3D11Texture2D Texture { get; }

    public uint Width { get; }

    public uint Height { get; }

    public int AdapterIndex { get; }

    public IntPtr SharedHandle { get; }

    public void Retain()
    {
        while (true)
        {
            var current = Volatile.Read(ref _referenceCount);
            if (current <= 0)
            {
                throw new ObjectDisposedException(nameof(GpuTextureFrame));
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
                _release?.Invoke();
            }

            return;
        }
    }
}
