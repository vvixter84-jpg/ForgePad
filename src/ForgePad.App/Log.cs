using System;
using System.Collections.Generic;
using System.IO;

namespace ForgePad.App;

/// <summary>
/// У оконного приложения нет консоли, поэтому ошибки пишем в файл.
/// Путь выбирается из нескольких кандидатов: рядом с exe может быть запрещено писать
/// (Program Files, защищённые папки), и тогда лог молча терялся.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string FilePath => _path ??= PickPath();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // Логирование не должно падать само.
            }
        }
    }

    public static void Error(Exception exception, string context)
    {
        Write($"ERROR [{context}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }

    private static string PickPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgePad"),
            AppContext.BaseDirectory,
            Path.GetTempPath(),
        };

        foreach (string directory in candidates)
        {
            if (string.IsNullOrEmpty(directory))
                continue;

            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "forgepad.log");

                // Проверяем запись сразу, иначе узнаем о проблеме только при падении.
                File.AppendAllText(path, "");

                return path;
            }
            catch
            {
                // пробуем следующий вариант
            }
        }

        return Path.Combine(Path.GetTempPath(), "forgepad.log");
    }
}
