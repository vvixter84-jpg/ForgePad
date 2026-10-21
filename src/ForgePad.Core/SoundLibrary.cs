using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForgePad.Core;

/// <summary>
/// Каталог звуков: файлы лежат в своей папке, метаданные — в library.json.
/// Имя файла на диске — хеш содержимого, поэтому переименование в интерфейсе
/// файл не трогает, а одинаковые файлы не дублируются.
/// </summary>
public sealed class SoundLibrary
{
    private static readonly string[] AudioExtensions = { ".wav", ".mp3", ".aiff", ".aif", ".m4a" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public List<SoundEntry> Entries { get; } = new();

    public static string RootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForgePad");

    public static string SoundsDir => Path.Combine(RootDir, "sounds");

    private static string IndexPath => Path.Combine(RootDir, "library.json");

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static SoundLibrary Load()
    {
        Directory.CreateDirectory(SoundsDir);

        var library = new SoundLibrary();

        if (File.Exists(IndexPath))
        {
            try
            {
                List<SoundEntry>? saved = JsonSerializer.Deserialize<List<SoundEntry>>(
                    File.ReadAllText(IndexPath), JsonOptions);

                if (saved is not null)
                    library.Entries.AddRange(saved);
            }
            catch
            {
                // Битый индекс не должен мешать запуску: начинаем с пустого каталога.
            }
        }

        library.BindPaths();

        // Первый запуск или пустой каталог: подбираем уже лежащие в папке файлы.
        if (library.Entries.Count == 0)
        {
            foreach (string file in Directory.GetFiles(SoundsDir).Where(IsAudioFile))
                library.AddEntry(file);

            library.Save();
        }

        return library;
    }

    /// <summary>Сохраняет каталог. Ошибка записи не должна ронять приложение.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(RootDir);
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(Entries, JsonOptions));
            BindPaths();
            return true;
        }
        catch
        {
            // Каталог остаётся в памяти: работа продолжится, потеряется только запись на диск.
            return false;
        }
    }

    /// <summary>Копирует файл в каталог и возвращает новую запись (или существующую при дубле).</summary>
    public SoundEntry Import(string sourcePath)
    {
        string hash = ComputeHash(sourcePath);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        string fileName = hash + extension;
        string target = Path.Combine(SoundsDir, fileName);

        if (!File.Exists(target))
            File.Copy(sourcePath, target);

        SoundEntry? existing = Entries.FirstOrDefault(e => e.FileName == fileName);
        if (existing is not null)
            return existing;

        var entry = new SoundEntry
        {
            Name = Path.GetFileNameWithoutExtension(sourcePath),
            FileName = fileName,
            AddedAt = DateTime.Now,
            FullPath = target,
        };

        Entries.Add(entry);
        return entry;
    }

    public void Remove(SoundEntry entry)
    {
        Entries.Remove(entry);

        // Файл удаляем только если на него больше никто не ссылается.
        if (entry.FileName.Length > 0 && Entries.All(e => e.FileName != entry.FileName))
        {
            try { File.Delete(Path.Combine(SoundsDir, entry.FileName)); } catch { }
        }
    }

    private void AddEntry(string filePath)
    {
        var entry = new SoundEntry
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            FileName = Path.GetFileName(filePath),
            AddedAt = File.GetLastWriteTime(filePath),
        };

        Entries.Add(entry);
    }

    private void BindPaths()
    {
        foreach (SoundEntry entry in Entries)
            entry.FullPath = Path.Combine(SoundsDir, entry.FileName);
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
