using System;
using System.IO;

namespace ForgePad.Core;

/// <summary>
/// Забирает у системы роль «микрофона по умолчанию» и возвращает обратно.
/// Состояние дублируется в файл: если процесс убьют без сторожа, --restore вернёт голос.
/// </summary>
public static class MicTakeover
{
    private static string? _stateDir;

    /// <summary>
    /// Папка состояния. AppData — основной вариант, временная папка — запасной:
    /// в ограниченных окружениях запись в профиль может быть запрещена.
    /// </summary>
    private static string StateDir
    {
        get
        {
            if (_stateDir is not null)
                return _stateDir;

            string[] candidates =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Path.GetTempPath(),
            };

            foreach (string root in candidates)
            {
                if (string.IsNullOrEmpty(root))
                    continue;

                try
                {
                    string dir = Path.Combine(root, "ForgePad");
                    Directory.CreateDirectory(dir);
                    _stateDir = dir;
                    return dir;
                }
                catch
                {
                    // пробуем следующий
                }
            }

            _stateDir = Path.GetTempPath();
            return _stateDir;
        }
    }

    private static string StatePath => Path.Combine(StateDir, "previous-mic.txt");

    public static string? PreviousDeviceId
    {
        get
        {
            try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : null; }
            catch { return null; }
        }
    }

    /// <summary>Запоминает текущий микрофон и делает дефолтным кабель. Возвращает ID прежнего.</summary>
    public static string? Acquire(string cableId)
    {
        string? previous = AudioDevicePolicy.GetDefaultCaptureId();

        // Прошлый запуск не успел вернуть микрофон — восстанавливаемся по файлу.
        if (previous is null || previous == cableId)
        {
            string? saved = PreviousDeviceId;
            previous = saved is not null && saved != cableId ? saved : null;
        }

        if (previous is null)
            return null;

        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(StatePath, previous);
        }
        catch
        {
            // Без файла останется только сторож, но перехват всё равно делаем.
        }

        AudioDevicePolicy.SetDefaultCapture(cableId);
        return previous;
    }

    /// <summary>Возвращает микрофон, который был до перехвата.</summary>
    /// <summary>
    /// Возвращает микрофон, который был до перехвата. Файл состояния не удаляем:
    /// это «последний известный живой микрофон», по нему восстанавливаемся после
    /// жёсткого падения, когда память процесса уже потеряна.
    /// </summary>
    public static void Release(string? previous)
    {
        if (previous is null)
            previous = PreviousDeviceId;

        if (previous is null)
            return;

        try { AudioDevicePolicy.SetDefaultCapture(previous); }
        catch { /* устройство могло исчезнуть — оставляем как есть */ }
    }
}
