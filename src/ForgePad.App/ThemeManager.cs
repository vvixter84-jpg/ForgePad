using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ForgePad.App;

/// <summary>Палитра темы. Цвета хранятся hex-строками, чтобы их было удобно править руками.</summary>
public sealed record AppTheme(
    string Key,
    string Title,
    string Bg,
    string Panel,
    string Border,
    string Tile,
    string Text,
    string Muted,
    string Accent,
    string AccentDim);

/// <summary>Меняет палитру на ходу: кисти в ресурсах мутируются, поэтому перерисовки не нужны.</summary>
public static class ThemeManager
{
    public static IReadOnlyList<AppTheme> All { get; } = new List<AppTheme>
    {
        new("night", "Ночная",
            "#0A0A0E", "#111119", "#1E1E2A", "#15151F", "#E8E8EE", "#6E6E80", "#5B8CFF", "#26324F"),

        new("oled", "OLED",
            "#000000", "#060606", "#171717", "#0B0B0B", "#F5F5F5", "#5C5C5C", "#22D3EE", "#0A3A42"),

        new("light", "Светлая",
            "#F2F4F8", "#FFFFFF", "#D9DEE8", "#FFFFFF", "#101521", "#64748B", "#2563EB", "#DCE7FF"),

        new("phoenix", "Феникс",
            "#17100C", "#1F1611", "#402A1F", "#241A14", "#FFEBDD", "#B08A72", "#FF7A18", "#4A2410"),

        new("marshmallow", "Маршмеллоу",
            "#FFF4F7", "#FFFFFF", "#F3D9E3", "#FFF9FB", "#3F2A33", "#9C7F8B", "#FF7EB6", "#FFE1EE"),

        new("red", "Красная",
            "#12060A", "#1A0A0F", "#3A1119", "#200C12", "#FFE6EA", "#B0737E", "#FF3B4E", "#4A1219"),

        new("blue", "Синяя",
            "#04101F", "#071A2E", "#12304F", "#0A2340", "#DDEBFF", "#6E8FB5", "#38BDF8", "#0E3A5C"),

        new("purple", "Фиолетовая",
            "#0D0718", "#150C24", "#2C1A45", "#1B1030", "#F0E7FF", "#917FB0", "#B26BFF", "#331A52"),

        new("green", "Зелёная",
            "#04120C", "#071C13", "#123A28", "#0A2A1C", "#DFFFEF", "#6E9C86", "#34D399", "#0F3B29"),
    };

    public static AppTheme Current { get; private set; } = All[0];

    public static event Action? Changed;

    public static AppTheme ByKey(string? key) =>
        All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        Set("BgBrush", theme.Bg);
        Set("PanelBrush", theme.Panel);
        Set("PanelBorderBrush", theme.Border);
        Set("TextBrush", theme.Text);
        Set("MutedBrush", theme.Muted);
        Set("AccentBrush", theme.Accent);
        Set("AccentDimBrush", theme.AccentDim);
        Set("TileBrush", theme.Tile);

        Changed?.Invoke();
    }

    /// <summary>
    /// Красит существующую кисть. Именно мутация, а не замена: на неё уже ссылаются
    /// StaticResource во всех стилях, и замена бы не применилась к живым элементам.
    /// </summary>
    private static void Set(string key, string hex)
    {
        if (Application.Current?.Resources[key] is not SolidColorBrush brush)
            return;

        Color color = Parse(hex);

        if (brush.IsFrozen)
        {
            Application.Current.Resources[key] = new SolidColorBrush(color);
            return;
        }

        brush.Color = color;
    }

    public static Color Parse(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
                return color;
        }
        catch
        {
            // Некорректный цвет — берём нейтральный.
        }

        return Color.FromRgb(0x5B, 0x8C, 0xFF);
    }

    /// <summary>Смешивает два цвета: t=0 даёт первый, t=1 — второй.</summary>
    public static Color Blend(Color from, Color to, double t) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * t),
        (byte)(from.G + (to.G - from.G) * t),
        (byte)(from.B + (to.B - from.B) * t));
}
