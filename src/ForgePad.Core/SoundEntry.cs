using System;
using System.IO;
using System.Text.Json.Serialization;

namespace ForgePad.Core;

/// <summary>Одна запись каталога: файл плюс имя, которое пользователь дал сам.</summary>
public sealed class SoundEntry
{
    /// <summary>Стабильный идентификатор: имя можно менять, id остаётся.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Отображаемое имя. Живёт только здесь, файл не переименовывается.</summary>
    public string Name { get; set; } = "";

    /// <summary>Имя файла внутри папки sounds.</summary>
    public string FileName { get; set; } = "";

    public float Gain { get; set; } = 1f;

    /// <summary>Привязка клавиши в текстовом виде: "F1", "Ctrl+F5", "Num3".</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>Акцентный цвет плитки в hex.</summary>
    public string Color { get; set; } = "#5B8CFF";

    public DateTime AddedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string FullPath { get; set; } = "";

    [JsonIgnore]
    public bool Missing => FileName.Length == 0 || !File.Exists(FullPath);

    /// <summary>Подпись для пустой плитки, если имя ещё не задано.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "без имени" : Name;
}
