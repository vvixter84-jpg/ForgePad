using System;
using System.IO;
using System.Text.Json;

namespace ForgePad.Core;

/// <summary>Настройки приложения, которые переживают перезапуск.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "night";

    /// <summary>Показывать ли подсказки при первом запуске.</summary>
    public bool ShowHints { get; set; } = true;

    private static string FilePath => Path.Combine(SoundLibrary.RootDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                AppSettings? saved = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));

                if (saved is not null)
                    return saved;
            }
        }
        catch
        {
            // Битые настройки не должны мешать запуску.
        }

        return new AppSettings();
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(SoundLibrary.RootDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
