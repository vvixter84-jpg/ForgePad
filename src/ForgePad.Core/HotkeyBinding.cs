using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgePad.Core;

/// <summary>
/// Привязка клавиши: модификаторы плюс виртуальный код Win32.
/// Хранится строкой, чтобы library.json оставался читаемым руками.
/// </summary>
public sealed class HotkeyBinding : IEquatable<HotkeyBinding>
{
    public const uint ModAlt = 1;
    public const uint ModControl = 2;
    public const uint ModShift = 4;

    /// <summary>Клавиши, занятые управлением приложения и недоступные для привязки.</summary>
    private static readonly Dictionary<int, string> Reserved = new()
    {
        [0x79] = "F10 (стоп)",
        [0x7A] = "F11 (монитор)",
    };

    private static readonly Dictionary<string, int> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73,
        ["F5"] = 0x74, ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77,
        ["F9"] = 0x78, ["F12"] = 0x7B,
        ["Num0"] = 0x60, ["Num1"] = 0x61, ["Num2"] = 0x62, ["Num3"] = 0x63, ["Num4"] = 0x64,
        ["Num5"] = 0x65, ["Num6"] = 0x66, ["Num7"] = 0x67, ["Num8"] = 0x68, ["Num9"] = 0x69,
        ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
        ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,
        ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45, ["F"] = 0x46,
        ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A, ["K"] = 0x4B, ["L"] = 0x4C,
        ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F, ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52,
        ["S"] = 0x53, ["T"] = 0x54, ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58,
        ["Y"] = 0x59, ["Z"] = 0x5A,
    };

    public HotkeyBinding(uint modifiers, int virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public uint Modifiers { get; }

    public int VirtualKey { get; }

    public bool IsEmpty => VirtualKey == 0;

    public static HotkeyBinding Empty { get; } = new(0, 0);

    /// <summary>Подпись для интерфейса: "Ctrl+F5".</summary>
    public override string ToString()
    {
        if (IsEmpty)
            return "";

        var parts = new List<string>();

        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        parts.Add(KeyName(VirtualKey));

        return string.Join("+", parts);
    }

    public static HotkeyBinding Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Empty;

        uint modifiers = 0;
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string keyPart = "";

        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) { modifiers |= ModControl; continue; }
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { modifiers |= ModAlt; continue; }
            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { modifiers |= ModShift; continue; }

            keyPart = part;
        }

        return ByName.TryGetValue(keyPart, out int vk) ? new HotkeyBinding(modifiers, vk) : Empty;
    }

    /// <summary>Можно ли назначить эту клавишу звуку.</summary>
    public static bool IsAllowed(HotkeyBinding binding, out string reason)
    {
        reason = "";

        if (binding.IsEmpty)
        {
            reason = "Нужна клавиша: F1..F9, F12, цифры, NumPad или буква.";
            return false;
        }

        if (binding.Modifiers == 0 && Reserved.TryGetValue(binding.VirtualKey, out string? reserved))
        {
            reason = $"Клавиша {reserved} занята управлением.";
            return false;
        }

        // Буквы и цифры без модификатора перехватывать нельзя: сломается печать в других окнах.
        if (binding.Modifiers == 0 && IsTyping(binding.VirtualKey))
        {
            reason = "Для букв и цифр нужен модификатор: Ctrl, Alt или Shift.";
            return false;
        }

        return true;
    }

    /// <summary>Модификаторы, которые сейчас реально зажаты.</summary>
    public static uint CurrentModifiers()
    {
        uint modifiers = 0;

        if (IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3)) modifiers |= ModControl;
        if (IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5)) modifiers |= ModAlt;
        if (IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1)) modifiers |= ModShift;

        return modifiers;
    }

    public static string KeyName(int virtualKey)
    {
        string? name = ByName.FirstOrDefault(pair => pair.Value == virtualKey).Key;
        return name ?? $"0x{virtualKey:X2}";
    }

    private static bool IsTyping(int virtualKey) =>
        virtualKey is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    public bool Equals(HotkeyBinding? other) =>
        other is not null && other.Modifiers == Modifiers && other.VirtualKey == VirtualKey;

    public override bool Equals(object? obj) => Equals(obj as HotkeyBinding);

    public override int GetHashCode() => HashCode.Combine(Modifiers, VirtualKey);
}
