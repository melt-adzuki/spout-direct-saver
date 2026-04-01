using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App.Services;

internal sealed class EncoderSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public string FilePath { get; }

    public EncoderSettingsStore()
    {
        FilePath = AppDataPaths.EncoderSettingsFilePath;
    }

    public EncoderSettingsRoot Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return EncoderSettingsRoot.CreateDefaults();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<EncoderSettingsRoot>(json, SerializerOptions)
                ?? EncoderSettingsRoot.CreateDefaults();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return EncoderSettingsRoot.CreateDefaults();
        }
    }

    public void Save(EncoderSettingsRoot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        File.WriteAllText(FilePath, json);
    }

}
