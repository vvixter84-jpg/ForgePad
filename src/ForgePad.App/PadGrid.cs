using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ForgePad.Core;

namespace ForgePad.App;

/// <summary>Плитка на сетке: запись каталога плюс её состояние.</summary>
public sealed class PadTile
{
    public required SoundEntry Entry { get; init; }

    /// <summary>0..1 — сколько уже проиграно. Ноль означает «молчит».</summary>
    public float Progress { get; set; }
}

/// <summary>
/// Сетка плиток рисуется вручную: так виртуализация получается бесплатно
/// (рисуются только видимые ряды) и вид не ограничен стандартными контролами.
/// </summary>
public sealed class PadGrid : FrameworkElement
{
    private const double TileWidth = 172;
    private const double TileHeight = 108;
    private const double Gap = 12;
    private const double Pad = 18;
    private const double Radius = 10;

    private readonly List<PadTile> _items = new();

    // Кисти пересобираются при смене темы: статическими они остаются только ради простоты.
    private static Brush Background = Brushes.Black;
    private static Brush TileFill = Brushes.Black;
    private static Brush TileFillHover = Brushes.Black;
    private static Brush TileFillPlaying = Brushes.Black;
    private static Brush TextBrush = Brushes.White;
    private static Brush MutedBrush = Brushes.Gray;
    private static Brush AccentBrush = Brushes.DodgerBlue;
    private static Brush DangerBrush = Brushes.IndianRed;
    private static Brush BadgeFill = Brushes.DimGray;

    private static Pen BorderPen = new(Brushes.DimGray, 1);
    private static Pen SelectedPen = new(Brushes.DodgerBlue, 1.6);
    private static Pen PlayingPen = new(Brushes.SteelBlue, 1);

    private static string _paletteKey = "";

    /// <summary>Пересобирает кисти под текущую палитру.</summary>
    private static void EnsurePalette()
    {
        AppTheme theme = ThemeManager.Current;

        if (_paletteKey == theme.Key)
            return;

        _paletteKey = theme.Key;

        Color bg = ThemeManager.Parse(theme.Bg);
        Color tile = ThemeManager.Parse(theme.Tile);
        Color text = ThemeManager.Parse(theme.Text);
        Color muted = ThemeManager.Parse(theme.Muted);
        Color accent = ThemeManager.Parse(theme.Accent);
        Color border = ThemeManager.Parse(theme.Border);
        Color panel = ThemeManager.Parse(theme.Panel);

        Background = Frozen(bg);
        TileFill = Frozen(tile);
        TileFillHover = Frozen(ThemeManager.Blend(tile, text, 0.07));
        TileFillPlaying = Frozen(ThemeManager.Blend(tile, accent, 0.18));
        TextBrush = Frozen(text);
        MutedBrush = Frozen(muted);
        AccentBrush = Frozen(accent);
        DangerBrush = Frozen(Color.FromRgb(0xFF, 0x5B, 0x6E));
        BadgeFill = Frozen(ThemeManager.Blend(panel, text, 0.12));

        BorderPen = FrozenPen(border, 1);
        SelectedPen = FrozenPen(accent, 1.6);
        PlayingPen = FrozenPen(ThemeManager.Blend(accent, border, 0.45), 1);
    }

    private int _hovered = -1;
    private int _selected = -1;
    private double _scroll;

    public PadGrid()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;

        ThemeManager.Changed += () =>
        {
            _paletteKey = "";
            EnsurePalette();
            InvalidateVisual();
        };
    }

    public IReadOnlyList<PadTile> Items => _items;

    public event Action<PadTile>? PlayRequested;

    public event Action<PadTile>? RenameRequested;

    public event Action<PadTile>? DeleteRequested;

    /// <summary>Пользователь выбрал плитку (или снял выбор).</summary>
    public event Action<PadTile?>? SelectionChanged;

    public PadTile? Selected =>
        _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    public void SetItems(IEnumerable<PadTile> tiles)
    {
        _items.Clear();
        _items.AddRange(tiles);
        _selected = -1;
        _scroll = 0;
        InvalidateVisual();
    }

    /// <summary>Выбрать плитку программно (например, после переименования).</summary>
    public void Select(PadTile? tile)
    {
        _selected = tile is null ? -1 : _items.IndexOf(tile);
        InvalidateVisual();
    }

    public void SetProgress(string path, float progress)
    {
        bool changed = false;

        foreach (PadTile tile in _items)
        {
            if (tile.Entry.FullPath != path)
                continue;

            if (Math.Abs(tile.Progress - progress) > 0.0005)
            {
                tile.Progress = progress;
                changed = true;
            }
        }

        if (changed)
            InvalidateVisual();
    }

    public void ClearProgress()
    {
        foreach (PadTile tile in _items)
            tile.Progress = 0;
    }

    /// <summary>Обновляет прогресс по карте «путь файла → 0..1».</summary>
    public void ApplyProgress(IReadOnlyDictionary<string, float> progressByPath)
    {
        bool changed = false;

        foreach (PadTile tile in _items)
        {
            float progress = progressByPath.TryGetValue(tile.Entry.FullPath, out float value)
                ? value
                : 0f;

            if (Math.Abs(tile.Progress - progress) > 0.0005f)
            {
                tile.Progress = progress;
                changed = true;
            }
        }

        if (changed)
            InvalidateVisual();
    }

    /// <summary>Экранные координаты плитки — чтобы поставить поверх неё поле переименования.</summary>
    public Rect GetTileBounds(PadTile tile)
    {
        int index = _items.IndexOf(tile);
        if (index < 0)
            return Rect.Empty;

        int columns = Columns;
        int row = index / columns;
        int column = index % columns;

        return new Rect(
            Pad + column * (TileWidth + Gap),
            Pad + row * (TileHeight + Gap) - _scroll,
            TileWidth,
            TileHeight);
    }

    private int Columns =>
        Math.Max(1, (int)((ActualWidth - 2 * Pad + Gap) / (TileWidth + Gap)));

    private double ContentHeight
    {
        get
        {
            int columns = Columns;
            int rows = (int)Math.Ceiling(_items.Count / (double)columns);
            return 2 * Pad + rows * (TileHeight + Gap);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        EnsurePalette();

        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_items.Count == 0)
        {
            DrawCentered(dc, "Пока пусто. Перетащи сюда звуки или нажми «Добавить».", MutedBrush, 14);
            return;
        }

        int columns = Columns;
        int firstRow = Math.Max(0, (int)((_scroll - Pad) / (TileHeight + Gap)));
        int lastRow = (int)((_scroll + ActualHeight) / (TileHeight + Gap)) + 1;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int index = 0; index < _items.Count; index++)
        {
            int row = index / columns;
            if (row < firstRow || row > lastRow)
                continue;

            int column = index % columns;

            var rect = new Rect(
                Pad + column * (TileWidth + Gap),
                Pad + row * (TileHeight + Gap) - _scroll,
                TileWidth,
                TileHeight);

            DrawTile(dc, rect, index, _items[index], dpi);
        }
    }

    private void DrawTile(DrawingContext dc, Rect rect, int index, PadTile tile, double dpi)
    {
        bool playing = tile.Progress > 0f;
        bool hovered = index == _hovered;
        bool selected = index == _selected;
        bool missing = tile.Entry.Missing;

        Brush fill = playing ? TileFillPlaying : hovered ? TileFillHover : TileFill;
        Pen pen = selected ? SelectedPen : playing ? PlayingPen : BorderPen;

        dc.DrawRoundedRectangle(fill, pen, rect, Radius, Radius);

        // Акцентная полоса цвета плитки.
        Color accent = ParseColor(tile.Entry.Color);
        var stripe = new Rect(rect.X + 12, rect.Y + 10, 18, 3);
        dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, stripe, 1.5, 1.5);

        // Индикатор состояния — точка рядом с полосой.
        var dot = new Point(rect.X + 14, rect.Y + 24);
        dc.DrawEllipse(missing ? DangerBrush : playing ? new SolidColorBrush(accent) : MutedBrush, null, dot, 3, 3);

        // Подпись горячей клавиши.
        string hotkey = tile.Entry.Hotkey;

        if (hotkey.Length > 0)
        {
            var badge = new Rect(rect.Right - 52, rect.Y + 9, 42, 16);
            dc.DrawRoundedRectangle(BadgeFill, null, badge, 4, 4);
            DrawText(dc, hotkey, badge.X + 5, badge.Y + 2, 9.5, playing ? AccentBrush : MutedBrush, dpi);
        }

        // Имя.
        FormattedText name = BuildText(
            tile.Entry.DisplayName,
            missing ? MutedBrush : TextBrush,
            12.5,
            dpi,
            TileWidth - 28);

        dc.DrawText(name, new Point(rect.X + 14, rect.Y + 44));

        // Длина файла или предупреждение.
        string hint = missing ? "файл не найден" : playing ? "играет" : "нажми, чтобы сыграть";
        DrawText(dc, hint, rect.X + 14, rect.Bottom - 24, 10.5, missing ? DangerBrush : MutedBrush, dpi);

        // Полоса прогресса.
        if (playing)
        {
            double width = (TileWidth - 28) * Math.Clamp(tile.Progress, 0f, 1f);
            var bar = new Rect(rect.X + 14, rect.Bottom - 8, Math.Max(2, width), 2.5);
            dc.DrawRoundedRectangle(AccentBrush, null, bar, 1.25, 1.25);
        }
    }

    private void DrawCentered(DrawingContext dc, string text, Brush brush, double size)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText formatted = BuildText(text, brush, size, dpi, ActualWidth - 40);

        dc.DrawText(formatted, new Point(
            (ActualWidth - formatted.Width) / 2,
            (ActualHeight - formatted.Height) / 2));
    }

    private static FormattedText BuildText(string text, Brush brush, double size, double dpi, double maxWidth) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            dpi)
        {
            MaxTextWidth = Math.Max(24, maxWidth),
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 1,
        };

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, double dpi) =>
        dc.DrawText(BuildText(text, brush, size, dpi, 400), new Point(x, y));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.GetPosition(this));

        if (index != _hovered)
        {
            _hovered = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovered != -1)
        {
            _hovered = -1;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        int index = IndexAt(e.GetPosition(this));
        if (index < 0)
            return;

        _selected = index;
        InvalidateVisual();
        SelectionChanged?.Invoke(_items[index]);

        if (e.ClickCount >= 2)
            RenameRequested?.Invoke(_items[index]);
        else
            PlayRequested?.Invoke(_items[index]);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        double maxScroll = Math.Max(0, ContentHeight - ActualHeight);
        _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * 64, 0, maxScroll);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        double maxScroll = Math.Max(0, ContentHeight - ActualHeight);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (_selected < 0 || _selected >= _items.Count)
            return;

        PadTile tile = _items[_selected];

        switch (e.Key)
        {
            case Key.F2:
                RenameRequested?.Invoke(tile);
                e.Handled = true;
                break;

            case Key.Delete:
                DeleteRequested?.Invoke(tile);
                e.Handled = true;
                break;

            case Key.Enter:
            case Key.Space:
                PlayRequested?.Invoke(tile);
                e.Handled = true;
                break;
        }
    }

    private int IndexAt(Point point)
    {
        if (_items.Count == 0)
            return -1;

        int columns = Columns;
        double localX = point.X - Pad;
        double localY = point.Y - Pad + _scroll;

        if (localX < 0 || localY < 0)
            return -1;

        int column = (int)(localX / (TileWidth + Gap));
        int row = (int)(localY / (TileHeight + Gap));

        if (column >= columns)
            return -1;

        // Внутри промежутка между плитками — не попадание.
        if (localX - column * (TileWidth + Gap) > TileWidth)
            return -1;

        if (localY - row * (TileHeight + Gap) > TileHeight)
            return -1;

        int index = row * columns + column;
        return index < _items.Count ? index : -1;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color color)
                return color;
        }
        catch
        {
            // Некорректный hex — берём акцент по умолчанию.
        }

        return Color.FromRgb(0x5B, 0x8C, 0xFF);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(Frozen(color), thickness);
        pen.Freeze();
        return pen;
    }
}
