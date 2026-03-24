using System;

namespace SpoutDirectSaver.App.Models;

internal sealed record FramePacket(
    PixelBufferLease PixelBuffer,
    CapturePixelFormat PixelFormat,
    uint Width,
    uint Height,
    string SenderName,
    double SenderFps,
    long StopwatchTicks,
    DateTimeOffset TimestampUtc);
