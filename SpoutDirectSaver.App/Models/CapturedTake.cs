using System;
using System.IO;
using System.Threading.Tasks;

namespace SpoutDirectSaver.App.Models;

internal sealed class CapturedTake : IAsyncDisposable
{
    public CapturedTake(
        EncoderOption encoderOption,
        EncoderSettingsRoot encoderSettings,
        string takeDirectory,
        string previewVideoPath,
        string? previewSidecarPath)
    {
        EncoderOption = encoderOption;
        EncoderSettings = encoderSettings.Clone();
        TakeDirectory = takeDirectory;
        PreviewVideoPath = previewVideoPath;
        PreviewSidecarPath = previewSidecarPath;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public EncoderOption EncoderOption { get; }

    public EncoderSettingsRoot EncoderSettings { get; }

    public string TakeDirectory { get; }

    public string PreviewVideoPath { get; }

    public string? PreviewSidecarPath { get; }

    public DateTimeOffset CreatedAt { get; }

    public bool HasSidecar => !string.IsNullOrWhiteSpace(PreviewSidecarPath);

    public ValueTask DisposeAsync()
    {
        TryDeleteDirectory(TakeDirectory);
        return ValueTask.CompletedTask;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Keep the take if cleanup fails.
        }
    }
}
