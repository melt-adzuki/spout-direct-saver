using System;

namespace SpoutDirectSaver.App.Models;

internal sealed class FramePacket : IDisposable
{
    public FramePacket(
        PixelBufferLease? pixelBuffer,
        GpuTextureFrame? gpuTexture,
        PixelBufferLease? alphaBuffer,
        uint width,
        uint height,
        string senderName,
        double senderFps,
        long stopwatchTicks,
        DateTimeOffset timestampUtc)
    {
        PixelBuffer = pixelBuffer;
        GpuTexture = gpuTexture;
        AlphaBuffer = alphaBuffer;
        Width = width;
        Height = height;
        SenderName = senderName;
        SenderFps = senderFps;
        StopwatchTicks = stopwatchTicks;
        TimestampUtc = timestampUtc;
    }

    public FramePacket(
        PixelBufferLease pixelBuffer,
        uint width,
        uint height,
        string senderName,
        double senderFps,
        long stopwatchTicks,
        DateTimeOffset timestampUtc)
        : this(
            pixelBuffer,
            null,
            null,
            width,
            height,
            senderName,
            senderFps,
            stopwatchTicks,
            timestampUtc)
    {
    }

    public PixelBufferLease? PixelBuffer { get; }

    public GpuTextureFrame? GpuTexture { get; }

    public PixelBufferLease? AlphaBuffer { get; }

    public uint Width { get; }

    public uint Height { get; }

    public string SenderName { get; }

    public double SenderFps { get; }

    public long StopwatchTicks { get; }

    public DateTimeOffset TimestampUtc { get; }

    public void Dispose()
    {
        PixelBuffer?.Dispose();
        GpuTexture?.Dispose();
        AlphaBuffer?.Dispose();
    }
}
