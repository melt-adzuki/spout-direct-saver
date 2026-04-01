using System;
using System.IO;

namespace SpoutDirectSaver.App.Services;

internal static class AppDataPaths
{
    public static string EncoderSettingsFilePath { get; } = BuildEncoderSettingsFilePath();

    public static string CacheRootDirectory { get; } = Path.Combine(Path.GetTempPath(), "SpoutDirectSaverCaches");

    public static void ClearCacheRoot()
    {
        TryDeleteDirectory(CacheRootDirectory);
    }

    private static string BuildEncoderSettingsFilePath()
    {
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var baseDirectory = string.IsNullOrWhiteSpace(roamingAppData)
            ? Path.Combine(Path.GetTempPath(), "SpoutDirectSaver")
            : Path.Combine(roamingAppData, "SpoutDirectSaver");

        return Path.Combine(baseDirectory, "encoder-settings.json");
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
            // Best-effort cleanup only.
        }
    }
}
