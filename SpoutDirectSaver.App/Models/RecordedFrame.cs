using System;

namespace SpoutDirectSaver.App.Models;

internal sealed class RecordedFrame
{
    public required int FrameIndex { get; init; }

    public required long StopwatchTicks { get; init; }

    public required DateTimeOffset TimestampUtc { get; init; }

    public double DurationSeconds { get; set; }

    public long SpoolOffset { get; set; }

    public int SpoolLength { get; set; }

    public bool IsCompressed { get; set; }

    public bool ReusePreviousSpoolFrame { get; set; }
}
