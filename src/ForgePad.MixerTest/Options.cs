using System;
using System.IO;
using System.Linq;
using ForgePad.Core;

namespace ForgePad.MixerTest;

/// <summary>Параметры запуска прототипа.</summary>
internal sealed record Options
{
    public bool ListOnly { get; init; }

    /// <summary>Открыть устройства и выйти, ничего не воспроизводя.</summary>
    public bool ProbeOnly { get; init; }

    /// <summary>Файл для сквозной проверки «сыграть в кабель и услышать его вход».</summary>
    public string? LoopbackFile { get; init; }

    /// <summary>Мерять уровни микрофона и кабеля, ничего не играя.</summary>
    public bool LevelOnly { get; init; }

    /// <summary>Играть пэды ещё и в наушники, чтобы слышать их самому.</summary>
    public bool Monitor { get; init; } = true;

    /// <summary>Громкость монитора.</summary>
    public float MonitorGain { get; init; } = 1f;

    /// <summary>Автоматически играть первый звук по кругу — для проверки без клавиатуры.</summary>
    public bool Demo { get; init; }

    /// <summary>Захватывать микрофон по умолчанию на время работы.</summary>
    public bool Takeover { get; init; } = true;

    /// <summary>Точный ID устройства захвата (чтобы не поймать собственный кабель).</summary>
    public string? MicDeviceId { get; init; }

    /// <summary>Режим сторожа: ждать этот процесс и вернуть микрофон.</summary>
    public int? WatchdogPid { get; init; }

    /// <summary>Устройство, которое сторож вернёт.</summary>
    public string? WatchdogRestore { get; init; }

    /// <summary>Вернуть микрофон по сохранённому состоянию и выйти.</summary>
    public bool RestoreOnly { get; init; }

    /// <summary>Аварийно вернуть конкретное устройство ввода по части имени.</summary>
    public string? RestoreTo { get; init; }

    /// <summary>Замер того, что реально звучит в устройстве вывода, с.</summary>
    public int? CheckOutputSeconds { get; init; }

    /// <summary>Слушать только вход кабеля, ничего не воспроизводя, с.</summary>
    public int? ListenSeconds { get; init; }

    /// <summary>Измерить задержку от запуска звука до появления его в кабеле.</summary>
    public string? LatencyFile { get; init; }

    /// <summary>Измерить задержку до устройства вывода (наушники).</summary>
    public string? LatencyRenderFile { get; init; }

    /// <summary>Проверить декодирование файла и выйти.</summary>
    public string? DecodeFile { get; init; }

    /// <summary>Проиграть файл и замерить, что пришло в кабель.</summary>
    public string? RenderCheckFile { get; init; }

    /// <summary>Подробный лог перехвата клавиш.</summary>
    public bool HotkeyDebug { get; init; }

    /// <summary>Длительность замера уровня, с.</summary>
    public int Seconds { get; init; } = 8;

    /// <summary>Запрошенная латентность вывода в кабель, мс.</summary>
    public int LatencyMs { get; init; } = 20;

    /// <summary>Размер блока захвата микрофона, мс.</summary>
    public int CaptureBufferMs { get; init; } = 10;

    public string SoundsDir { get; init; } = "";

    public float PadGain { get; init; } = 1f;

    public string? MicHint { get; init; }

    public string? CableHint { get; init; } = "CABLE Input";

    public static Options Parse(string[] args)
    {
        bool list = false;
        bool probe = false;
        bool level = false;
        bool hotkeyDebug = false;
        bool demo = false;
        bool monitor = true;
        float monitorGain = 1f;
        bool restore = false;
        string? restoreTo = null;
        int? checkOutput = null;
        int? listen = null;
        string? latencyFile = null;
        string? latencyRenderFile = null;
        string? decodeFile = null;
        string? renderCheckFile = null;
        bool takeover = true;
        string? micId = null;
        int? watchdogPid = null;
        string? watchdogRestore = null;
        int seconds = 8;
        string? loopback = null;
        int latency = 20;
        int captureBuffer = 10;
        float padGain = 1f;
        string sounds = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ForgePad", "sounds");
        string? mic = null;
        string? cable = "CABLE Input";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--list":
                    list = true;
                    break;
                case "--probe":
                    probe = true;
                    break;
                case "--loopback" when i + 1 < args.Length:
                    loopback = Path.GetFullPath(args[++i]);
                    break;
                case "--level":
                    level = true;
                    break;
                case "--hotkey-debug":
                    hotkeyDebug = true;
                    break;
                case "--demo":
                    demo = true;
                    break;
                case "--no-monitor":
                    monitor = false;
                    break;
                case "--monitor-gain" when i + 1 < args.Length:
                    monitorGain = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--restore":
                    restore = true;
                    break;
                case "--restore-to" when i + 1 < args.Length:
                    restoreTo = args[++i];
                    break;
                case "--check-output":
                    checkOutput = i + 1 < args.Length && int.TryParse(args[i + 1], out int secs) ? (int?)secs : 5;
                    if (checkOutput is not null && i + 1 < args.Length) i++;
                    break;
                case "--listen":
                    listen = i + 1 < args.Length && int.TryParse(args[i + 1], out int lsecs) ? (int?)lsecs : 4;
                    if (listen is not null && i + 1 < args.Length) i++;
                    break;
                case "--latency-test" when i + 1 < args.Length:
                    latencyFile = Path.GetFullPath(args[++i]);
                    break;
                case "--decode" when i + 1 < args.Length:
                    decodeFile = Path.GetFullPath(args[++i]);
                    break;
                case "--render-check" when i + 1 < args.Length:
                    renderCheckFile = Path.GetFullPath(args[++i]);
                    break;
                case "--latency-render" when i + 1 < args.Length:
                    latencyRenderFile = Path.GetFullPath(args[++i]);
                    break;
                case "--no-takeover":
                    takeover = false;
                    break;
                case "--mic-id" when i + 1 < args.Length:
                    micId = args[++i];
                    break;
                case "--watchdog" when i + 2 < args.Length:
                    watchdogPid = int.Parse(args[++i]);
                    watchdogRestore = args[++i];
                    break;
                case "--seconds" when i + 1 < args.Length:
                    seconds = int.Parse(args[++i]);
                    break;
                case "--latency" when i + 1 < args.Length:
                    latency = int.Parse(args[++i]);
                    break;
                case "--capture" when i + 1 < args.Length:
                    captureBuffer = int.Parse(args[++i]);
                    break;
                case "--sounds" when i + 1 < args.Length:
                    sounds = Path.GetFullPath(args[++i]);
                    break;
                case "--gain" when i + 1 < args.Length:
                    padGain = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--mic" when i + 1 < args.Length:
                    mic = args[++i];
                    break;
                case "--cable" when i + 1 < args.Length:
                    cable = args[++i];
                    break;
            }
        }

        return new Options
        {
            ListOnly = list,
            ProbeOnly = probe,
            LoopbackFile = loopback,
            LevelOnly = level,
            HotkeyDebug = hotkeyDebug,
            Demo = demo,
            Monitor = monitor,
            MonitorGain = monitorGain,
            Takeover = takeover,
            MicDeviceId = micId,
            WatchdogPid = watchdogPid,
            WatchdogRestore = watchdogRestore,
            RestoreOnly = restore,
            RestoreTo = restoreTo,
            CheckOutputSeconds = checkOutput,
            ListenSeconds = listen,
            LatencyFile = latencyFile,
            LatencyRenderFile = latencyRenderFile,
            DecodeFile = decodeFile,
            RenderCheckFile = renderCheckFile,
            Seconds = seconds,
            LatencyMs = latency,
            CaptureBufferMs = captureBuffer,
            SoundsDir = sounds,
            PadGain = padGain,
            MicHint = mic,
            CableHint = cable,
        };
    }

    /// <summary>Перевод параметров командной строки в настройки ядра.</summary>
    public AudioSettings ToAudioSettings() => new()
    {
        MicDeviceId = MicDeviceId,
        MicHint = MicHint,
        CableHint = CableHint ?? "CABLE Input",
        LatencyMs = LatencyMs,
        CaptureBufferMs = CaptureBufferMs,
        PadGain = PadGain,
        Monitor = Monitor,
        MonitorGain = MonitorGain,
    };

    public static bool IsAudioFile(string path) =>
        new[] { ".wav", ".mp3", ".aiff", ".aif", ".m4a" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
