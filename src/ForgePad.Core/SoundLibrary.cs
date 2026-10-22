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

    private static readonly Lazy<string> RootLazy = new(ResolveRoot);

    /// <summary>
    /// Папка данных. Выбирается по факту возможности записи: «Документы» на некоторых
    /// системах закрыты политиками или защитой от вымогателей, а неработающий импорт
    /// с непонятной ошибкой — худший вариант.
    /// </summary>
    public static string RootDir => RootLazy.Value;

    public static string SoundsDir => Path.Combine(RootDir, "sounds");

    private static string IndexPath => Path.Combine(RootDir, "library.json");

    public static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string ResolveRoot()
    {
        string baseDir = AppContext.BaseDirectory;

        // Портативный режим: данные рядом с программой (удобно с флешки).
        if (File.Exists(Path.Combine(baseDir, "portable.txt")))
            return EnsureWritable(Path.Combine(baseDir, "data")) ?? baseDir;

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgePad");

        string documents = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForgePad");

        string temp = Path.Combine(Path.GetTempPath(), "ForgePad");

        foreach (string candidate in new[] { local, documents, temp })
        {
            string? writable = EnsureWritable(candidate);

            if (writable is null)
                continue;

            // Перенесли профиль в LocalAppData — забираем с собой прежние звуки.
            if (!string.Equals(writable, documents, StringComparison.OrdinalIgnoreCase))
                Migrate(documents, writable);

            return writable;
        }

        return EnsureWritable(Path.Combine(baseDir, "data")) ?? temp;
    }

    /// <summary>Проверяет запись реальным файлом, а не наличием папки.</summary>
    private static string? EnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return directory;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Копирует каталог и звуки из прежней папки, не затирая существующее.</summary>
    private static void Migrate(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
                return;

            if (File.Exists(Path.Combine(to, "library.json")))
                return;

            Directory.CreateDirectory(to);

            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);

            string fromSounds = Path.Combine(from, "sounds");

            if (!Directory.Exists(fromSounds))
                return;

            string toSounds = Path.Combine(to, "sounds");
            Directory.CreateDirectory(toSounds);

            foreach (string file in Directory.GetFiles(fromSounds))
                File.Copy(file, Path.Combine(toSounds, Path.GetFileName(file)), overwrite: false);
        }
        catch
        {
            // Перенос — вспомогательный шаг: если не вышло, работаем с пустым каталогом.
        }
    }

    public static SoundLibrary Load()
    {
        try { Directory.CreateDirectory(SoundsDir); } catch { /* папку создаст импорт */ }

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

    /// <summary>
    /// Добавляет файл в каталог: копирует в библиотеку, а если копирование запрещено —
    /// подключает по исходному пути, чтобы звук всё равно работал.
    /// </summary>
    public SoundEntry Import(string sourcePath)
    {
        string hash = ComputeHash(sourcePath);
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        string fileName = hash + extension;
        string target = Path.Combine(SoundsDir, fileName);

        SoundEntry? existing = Entries.FirstOrDefault(e => e.FileName == fileName);
        if (existing is not null)
            return existing;

        if (File.Exists(target))
        {
            var ready = new SoundEntry
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                FileName = fileName,
                AddedAt = DateTime.Now,
                FullPath = target,
            };

            Entries.Add(ready);
            return ready;
        }

        try
        {
            Directory.CreateDirectory(SoundsDir);
            File.Copy(sourcePath, target);

            var copied = new SoundEntry
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                FileName = fileName,
                AddedAt = DateTime.Now,
                FullPath = target,
            };

            Entries.Add(copied);
            return copied;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Папка библиотеки недоступна — работаем ссылкой на исходный файл.
            var linked = new SoundEntry
            {
                Name = Path.GetFileNameWithoutExtension(sourcePath),
                FileName = "",
                ExternalPath = sourcePath,
                AddedAt = DateTime.Now,
                FullPath = sourcePath,
            };

            Entries.Add(linked);
            return linked;
        }
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
        {
            entry.FullPath = string.IsNullOrEmpty(entry.ExternalPath)
                ? Path.Combine(SoundsDir, entry.FileName)
                : entry.ExternalPath;
        }
    }

    private static string ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
