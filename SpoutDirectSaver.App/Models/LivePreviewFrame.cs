using System;

namespace SpoutDirectSaver.App.Models;

internal readonly record struct LivePreviewFrame(
    IntPtr PixelData,
    uint Width,
    uint Height,
    string SenderName,
    double SenderFps,
    long StopwatchTicks);
