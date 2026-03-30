using System;
using System.Threading;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;
using Vortice.Direct3D11;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeRgbNvencWriter : IAsyncDisposable
{
    private readonly MediaFoundationHevcWriter _writer;
    private bool _completed;
    private bool _disposed;

    public RealtimeRgbNvencWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        ID3D11Device? device = null)
    {
        _writer = new MediaFoundationHevcWriter(width, height, frameRate, outputPath, device);
    }

    public void QueueFrame(PixelBufferLease pixelData, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            pixelData.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            pixelData.Dispose();
            throw new InvalidOperationException("Realtime RGB writer は既に完了しています。");
        }

        try
        {
            _writer.WriteFrame(pixelData.Buffer, pixelData.Length, repeatCount);
        }
        finally
        {
            pixelData.Dispose();
        }
    }

    public void QueueFrame(GpuTextureFrame gpuFrame, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            gpuFrame.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            gpuFrame.Dispose();
            throw new InvalidOperationException("Realtime RGB writer は既に完了しています。");
        }

        try
        {
            _writer.WriteTextureFrame(gpuFrame.Texture, repeatCount);
        }
        finally
        {
            gpuFrame.Dispose();
        }
    }

    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (_completed)
        {
            return Task.CompletedTask;
        }

        _completed = true;
        _writer.Complete();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writer.Dispose();
        return ValueTask.CompletedTask;
    }
}
