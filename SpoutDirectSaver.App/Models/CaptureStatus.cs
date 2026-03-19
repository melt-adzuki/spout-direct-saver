namespace SpoutDirectSaver.App.Models;

internal sealed record CaptureStatus(
    bool IsConnected,
    string SenderName,
    uint Width,
    uint Height,
    double SenderFps,
    string Message);
