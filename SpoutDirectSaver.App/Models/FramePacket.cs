using System;

namespace SpoutDirectSaver.App.Models;

internal sealed record FramePacket(
    byte[] PixelData,
    uint Width,
    uint Height,
    string SenderName,
    double SenderFps,
    long StopwatchTicks,
    DateTimeOffset TimestampUtc);
