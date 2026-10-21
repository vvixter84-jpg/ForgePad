using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ForgePad.Core;
using Microsoft.Win32;

namespace ForgePad.App;

public partial class MainWindow : Window
{
    private static readonly string[] Palette =
    {
        "#5B8CFF", "#4CD08A", "#FFB020", "#FF5B6E", "#B36BFF", "#38D6E0", "#FF8A3D", "#9AA3B2",
    };

    private readonly SoundLibrary _library;
    private readonly DispatcherTimer _timer;
    private readonly List<PadTile> _tiles = new();
    private readonly Dictionary<HotkeyBinding, SoundEntry> _bindings = new();
    private readonly List<Border> _swatches = new();
    private readonly bool _takeover = true;

    private MixerEngine? _engine;
    private GlobalHotkeys? _hotkeys;
    private Thread? _audioThread;
    private volatile bool _audioStop;
    private string? _micTakenFrom;
    private PadTile? _renaming;
    private PadTile? _editing;
    private bool _monitorOn = true;
    private bool _capturingHotkey;
    private bool _suspendInspector;

    public MainWindow(bool selfCheck = false)
    {
        _takeover = !selfCheck;

        InitializeComponent();

        _library = SoundLibrary.Load();
        EnsureDefaultHotkeys();

        Pads.PlayRequested += PlayTile;
        Pads.RenameRequested += BeginRename;
        Pads.DeleteRequested += DeleteTile;
        Pads.SelectionChanged += OnTileSelected;

        BuildColorSwatches();
        ShowSearchHint();
        RebuildTiles();
        StartAudio();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += OnTick;
        _timer.Start();

        UpdateStatus();
    }

    // ---------- каталог и привязки ----------

    /// <summary>Первым звукам без клавиши выдаём свободные F1..F9, F12.</summary>
    private void EnsureDefaultHotkeys()
    {
        string[] defaults = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F12" };
        int next = 0;
        bool changed = false;

        foreach (SoundEntry entry in _library.Entries)
        {
            if (!HotkeyBinding.Parse(entry.Hotkey).IsEmpty)
                continue;

            while (next < defaults.Length &&
                   _library.Entries.Any(e => string.Equals(e.Hotkey, defaults[next], StringComparison.OrdinalIgnoreCase)))
            {
                next++;
            }

            if (next >= defaults.Length)
                break;

            entry.Hotkey = defaults[next];
            next++;
            changed = true;
        }

        if (changed)
            _library.Save();
    }

    private void RebuildBindings()
    {
        _bindings.Clear();

        foreach (SoundEntry entry in _library.Entries)
        {
            HotkeyBinding binding = HotkeyBinding.Parse(entry.Hotkey);

            if (binding.IsEmpty || _bindings.ContainsKey(binding))
                continue;

            _bindings[binding] = entry;
        }
    }

    private void RebuildTiles()
    {
        string? selectedId = _editing?.Entry.Id;
        string query = SearchBox.Text.Trim();

        _tiles.Clear();

        foreach (SoundEntry entry in _library.Entries)
        {
            if (query.Length > 0 && !entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            _tiles.Add(new PadTile { Entry = entry });
        }

        RebuildBindings();
        Pads.SetItems(_tiles);

        if (selectedId is not null)
        {
            PadTile? again = _tiles.FirstOrDefault(t => t.Entry.Id == selectedId);

            if (again is not null)
            {
                Pads.Select(again);
                _editing = again;
            }
        }

        ShowSearchHint();
    }

    // ---------- инспектор ----------

    private void BuildColorSwatches()
    {
        foreach (string hex in Palette)
        {
            var swatch = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Tag = hex,
            };

            string captured = hex;
            swatch.MouseLeftButtonDown += (_, _) => ApplyColor(captured);

            ColorPanel.Children.Add(swatch);
            _swatches.Add(swatch);
        }
    }

    private void ApplyColor(string hex)
    {
        if (_editing is null)
            return;

        _editing.Entry.Color = hex;
        _library.Save();
        HighlightColor(hex);
        Pads.InvalidateVisual();
    }

    private void HighlightColor(string hex)
    {
        foreach (Border swatch in _swatches)
        {
            bool active = string.Equals(swatch.Tag as string, hex, StringComparison.OrdinalIgnoreCase);
            swatch.BorderBrush = active ? Brushes.White : Brushes.Transparent;
        }
    }

    private void OnTileSelected(PadTile? tile)
    {
        _editing = tile;

        if (tile is null)
        {
            InspectorBody.Visibility = Visibility.Collapsed;
            InspectorHint.Visibility = Visibility.Visible;
            return;
        }

        _suspendInspector = true;
        NameBox.Text = tile.Entry.Name;
        HotkeyButton.Content = string.IsNullOrWhiteSpace(tile.Entry.Hotkey) ? "назначить" : tile.Entry.Hotkey;
        GainSlider.Value = tile.Entry.Gain;
        _suspendInspector = false;

        HighlightColor(tile.Entry.Color);
        InspectorBody.Visibility = Visibility.Visible;
        InspectorHint.Visibility = Visibility.Collapsed;
    }

    private void CommitName()
    {
        if (_editing is null || _suspendInspector)
            return;

        string name = NameBox.Text.Trim();

        if (name.Length == 0 || name == _editing.Entry.Name)
            return;

        _editing.Entry.Name = name;
        _library.Save();
        RebuildTiles();
    }

    private void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitName();
            Pads.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            OnTileSelected(_editing);
            Pads.Focus();
            e.Handled = true;
        }
    }

    private void OnNameLostFocus(object sender, RoutedEventArgs e) => CommitName();

    private void OnGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suspendInspector || _editing is null)
            return;

        _editing.Entry.Gain = (float)e.NewValue;
        _library.Save();
    }

    private void OnHotkeyButtonClick(object sender, RoutedEventArgs e)
    {
        if (_editing is null)
            return;

        _capturingHotkey = true;
        HotkeyButton.Content = "нажми клавишу…";
        Focus();
    }

    private void CancelCapture()
    {
        _capturingHotkey = false;

        if (_editing is not null)
            HotkeyButton.Content = string.IsNullOrWhiteSpace(_editing.Entry.Hotkey)
                ? "назначить"
                : _editing.Entry.Hotkey;
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_editing is not null)
            DeleteTile(_editing);
    }

    // ---------- горячие клавиши ----------

    private static uint ToModifiers(ModifierKeys modifiers)
    {
        uint result = 0;

        if ((modifiers & ModifierKeys.Control) != 0) result |= HotkeyBinding.ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= HotkeyBinding.ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= HotkeyBinding.ModShift;

        return result;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_capturingHotkey)
        {
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                CancelCapture();
                return;
            }

            // Модификатор сам по себе ничего не назначает — ждём клавишу.
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                return;
            }

            int virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
            var binding = new HotkeyBinding(ToModifiers(Keyboard.Modifiers), virtualKey);

            if (!HotkeyBinding.IsAllowed(binding, out string reason))
            {
                HotkeyButton.Content = reason;
                return;
            }

            if (_editing is not null)
            {
                // Освобождаем клавишу у других звуков, иначе получится двойное срабатывание.
                foreach (SoundEntry other in _library.Entries)
                {
                    if (other.Id != _editing.Entry.Id && binding.Equals(HotkeyBinding.Parse(other.Hotkey)))
                        other.Hotkey = "";
                }

                _editing.Entry.Hotkey = binding.ToString();
                _library.Save();
            }

            CancelCapture();
            RebuildTiles();
            return;
        }

        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OnAddClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnHotkey(int virtualKey, uint modifiers)
    {
        if (_capturingHotkey)
            return;

        Dispatcher.BeginInvoke(() => HandleHotkey(virtualKey, modifiers));
    }

    private void HandleHotkey(int virtualKey, uint modifiers)
    {
        if (modifiers == 0 && virtualKey == 0x79) // F10
        {
            _engine?.StopAll();
            return;
        }

        if (modifiers == 0 && virtualKey == 0x7A) // F11
        {
            ToggleMonitor();
            return;
        }

        var binding = new HotkeyBinding(modifiers, virtualKey);

        if (_bindings.TryGetValue(binding, out SoundEntry? entry))
            Play(entry);
    }

    // ---------- воспроизведение ----------

    private void PlayTile(PadTile tile) => Play(tile.Entry);

    private void Play(SoundEntry entry)
    {
        if (_engine is null)
        {
            TitleHint.Text = "аудио не запущено";
            return;
        }

        if (entry.Missing)
            return;

        string path = entry.FullPath;
        float gain = Math.Clamp(entry.Gain, 0f, 1.5f);
        MixerEngine engine = _engine;

        Log.Write($"play: {entry.DisplayName}");

        // Декодирование файла не должно подвешивать интерфейс.
        _ = Task.Run(() =>
        {
            try { engine.PlaySound(path, gain); }
            catch (Exception ex) { Log.Error(ex, "PlaySound"); }
        });
    }

    public void PlayFirstForTest()
    {
        if (Pads.Items.Count > 0)
            PlayTile(Pads.Items[0]);
    }

    private void ToggleMonitor()
    {
        if (_engine is null)
            return;

        _monitorOn = _engine.ToggleMonitor();
        UpdateStatus();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_engine is null)
            return;

        IReadOnlyList<(string Name, float Progress)> active = _engine.SnapshotVoices();
        var map = new Dictionary<string, float>(active.Count);

        foreach ((string name, float progress) in active)
            map[name] = progress;

        Pads.ApplyProgress(map);
    }

    // ---------- файлы и правка ----------

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Добавить звуки",
            Filter = "Звуки|*.wav;*.mp3;*.aiff;*.aif;*.m4a|Все файлы|*.*",
        };

        if (dialog.ShowDialog(this) == true)
            ImportFiles(dialog.FileNames);
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        int before = _library.Entries.Count;

        foreach (string path in paths)
        {
            if (!SoundLibrary.IsAudioFile(path))
                continue;

            _library.Import(path);
        }

        if (_library.Entries.Count == before)
            return;

        _library.Save();
        EnsureDefaultHotkeys();
        RebuildTiles();
        UpdateStatus();
    }

    private void OnFilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            ImportFiles(paths);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void BeginRename(PadTile tile)
    {
        Rect bounds = Pads.GetTileBounds(tile);
        if (bounds.IsEmpty)
            return;

        _renaming = tile;

        RenameBox.Width = bounds.Width - 20;
        Canvas.SetLeft(RenameBox, bounds.X + 10);
        Canvas.SetTop(RenameBox, bounds.Y + 44);
        RenameBox.Text = tile.Entry.Name;
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void CommitRename(bool accept)
    {
        if (_renaming is null)
            return;

        PadTile tile = _renaming;
        _renaming = null;
        RenameBox.Visibility = Visibility.Collapsed;

        if (!accept)
            return;

        string name = RenameBox.Text.Trim();

        if (name.Length == 0)
            return;

        tile.Entry.Name = name;
        _library.Save();
        RebuildTiles();
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitRename(false);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object sender, RoutedEventArgs e) => CommitRename(true);

    private void DeleteTile(PadTile tile)
    {
        MessageBoxResult answer = MessageBox.Show(
            this,
            $"Удалить «{tile.Entry.DisplayName}» из каталога?",
            "ForgePad",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        _library.Remove(tile.Entry);
        _library.Save();

        _editing = null;
        RebuildTiles();
        OnTileSelected(null);
        UpdateStatus();
    }

    // ---------- разметка ----------

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        RebuildTiles();
    }

    private void ShowSearchHint() =>
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnStopClick(object sender, RoutedEventArgs e) => _engine?.StopAll();

    private void OnMonitorClick(object sender, RoutedEventArgs e) => ToggleMonitor();

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        AppTheme current = ThemeManager.Current;

        var menu = new ContextMenu
        {
            PlacementTarget = ThemeButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Style = TryFindResource("ThemedMenu") as Style,
            Background = new SolidColorBrush(ThemeManager.Parse(current.Panel)),
            BorderBrush = new SolidColorBrush(ThemeManager.Parse(current.Border)),
            Foreground = new SolidColorBrush(ThemeManager.Parse(current.Text)),
        };

        foreach (AppTheme theme in ThemeManager.All)
        {
            var item = new MenuItem
            {
                Header = theme.Title,
                IsChecked = theme.Key == current.Key,
                Style = TryFindResource("ThemedMenuItem") as Style,
                Foreground = new SolidColorBrush(ThemeManager.Parse(current.Text)),
            };

            AppTheme captured = theme;
            item.Click += (_, _) => ApplyTheme(captured);

            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static void ApplyTheme(AppTheme theme)
    {
        ThemeManager.Apply(theme);

        AppSettings settings = AppSettings.Load();
        settings.Theme = theme.Key;
        settings.Save();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateStatus()
    {
        string cable = _engine?.CableName ?? "нет устройства";
        string monitor = _monitorOn ? "вкл" : "выкл";

        StatusText.Text =
            $"{_library.Entries.Count} звуков · канал: {cable} · монитор: {monitor} · F10 стоп · F11 монитор";
    }

    // ---------- аудио ----------

    private void StartAudio()
    {
        try
        {
            string? cableId = AudioDevicePolicy.FindCaptureId("CABLE Output");
            string? physical = AudioDevicePolicy.GetDefaultCaptureId();

            if (cableId is not null && physical == cableId)
            {
                string? saved = MicTakeover.PreviousDeviceId;

                if (saved is not null && saved != cableId)
                {
                    AudioDevicePolicy.SetDefaultCapture(saved);
                    physical = saved;
                }
            }

            var settings = new AudioSettings
            {
                MicDeviceId = physical is not null && physical != cableId ? physical : null,
                Monitor = true,
            };

            Exception? failure = null;
            using var ready = new ManualResetEventSlim(false);

            // WPF-поток работает в STA, а объекты NAudio живут в MTA: созданные здесь
            // они «стартуют», но в кабель ничего не пишут. Поэтому ядро — в своём потоке.
            _audioThread = new Thread(() =>
            {
                try
                {
                    _engine = new MixerEngine(settings);
                    _engine.Start();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    ready.Set();
                }

                while (!_audioStop)
                    Thread.Sleep(200);
            })
            {
                IsBackground = true,
                Name = "forgepad-audio",
            };

            _audioThread.SetApartmentState(ApartmentState.MTA);
            _audioThread.Start();
            ready.Wait(TimeSpan.FromSeconds(15));

            if (failure is not null)
                throw failure;

            Log.Write($"audio ok: mic={_engine!.MicName}, cable={_engine.CableName}, monitor={_engine.MonitorName ?? "нет"}");

            if (_takeover && cableId is not null)
            {
                _micTakenFrom = MicTakeover.Acquire(cableId);
                Log.Write($"takeover: from={_micTakenFrom}, cable={cableId}");

                if (_micTakenFrom is not null)
                    SpawnWatchdog(_micTakenFrom);
            }

            _hotkeys = new GlobalHotkeys(OnHotkey);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "StartAudio");
            TitleHint.Text = "аудио не запустилось — подробности в окне";

            MessageBox.Show(
                this,
                "Не удалось открыть аудио:\n\n" + ex.Message +
                "\n\nПроверь, что установлен VB-Cable и кабель не занят эксклюзивно.",
                "ForgePad",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void SpawnWatchdog(string restoreDeviceId)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId} \"{restoreDeviceId}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // Без сторожа останется только ручное восстановление.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();

        try { _library.Save(); } catch { }
        try { _engine?.StopAll(); } catch { }
        try { _hotkeys?.Dispose(); } catch { }

        _audioStop = true;
        try { _audioThread?.Join(1000); } catch { }

        if (_micTakenFrom is not null)
            MicTakeover.Release(_micTakenFrom);

        _engine?.Dispose();
        base.OnClosed(e);
    }
}
