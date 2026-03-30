using System;
using Vortice.Direct3D11;

namespace SpoutDirectSaver.App.Models;

internal sealed class GpuTextureFrame : IDisposable
{
    private readonly Action? _release;
    private bool _disposed;

    public GpuTextureFrame(
        ID3D11Device device,
        ID3D11Texture2D texture,
        uint width,
        uint height,
        Action? release)
    {
        Device = device;
        Texture = texture;
        Width = width;
        Height = height;
        _release = release;
    }

    public ID3D11Device Device { get; }

    public ID3D11Texture2D Texture { get; }

    public uint Width { get; }

    public uint Height { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _release?.Invoke();
    }
}
