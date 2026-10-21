using System;
using System.IO;

namespace ForgePad.App;

/// <summary>
/// У оконного приложения нет консоли, поэтому любые ошибки пишем в файл —
/// иначе сбой выглядит как «просто не работает».
/// </summary>
internal static class Log
{
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "forgepad.log");

    public static void Write(string message)
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

    public static void Error(Exception exception, string context)
    {
        Write($"ERROR [{context}] {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception}");
    }
}
