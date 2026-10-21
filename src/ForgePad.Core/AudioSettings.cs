namespace ForgePad.Core;

/// <summary>
/// Настройки аудио для <see cref="MixerEngine"/>. Отделены от параметров командной строки,
/// чтобы одно и то же ядро годилось и консоли, и приложению с интерфейсом.
/// </summary>
public sealed record AudioSettings
{
    /// <summary>Точный ID микрофона; важнее подсказки по имени.</summary>
    public string? MicDeviceId { get; init; }

    /// <summary>Часть имени микрофона, если ID неизвестен.</summary>
    public string? MicHint { get; init; }

    /// <summary>Часть имени выходного устройства (виртуальный кабель).</summary>
    public string CableHint { get; init; } = "CABLE Input";

    public int LatencyMs { get; init; } = 20;

    public int CaptureBufferMs { get; init; } = 10;

    public float PadGain { get; init; } = 1f;

    /// <summary>Играть ли звуки ещё и в наушники.</summary>
    public bool Monitor { get; init; } = true;

    public float MonitorGain { get; init; } = 1f;
}
