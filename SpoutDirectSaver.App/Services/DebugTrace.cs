using System;
using System.IO;
using System.Threading;

namespace SpoutDirectSaver.App.Services;

internal static class DebugTrace
{
    private static readonly object Gate = new();

    public static void WriteLine(string category, string message)
    {
        var path = Environment.GetEnvironmentVariable("SPOUT_DIRECT_SAVER_TRACE_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} [T{Environment.CurrentManagedThreadId}] {category}: {message}{Environment.NewLine}";
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line);
        }
    }
}
