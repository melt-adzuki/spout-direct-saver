using System;

namespace SpoutDirectSaver.App.Models;

internal sealed class RecordedFrame
{
    public required string FileName { get; init; }

    public required string AbsolutePath { get; init; }

    public required long StopwatchTicks { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public double DurationSeconds { get; set; }
}
