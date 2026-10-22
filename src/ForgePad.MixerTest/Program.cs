using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ForgePad.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ForgePad.MixerTest;

internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        try
        {
            Options opts = Options.Parse(args);
            GlobalHotkeys.Verbose = opts.HotkeyDebug;

            if (opts.WatchdogPid is int watchdogPid && opts.WatchdogRestore is string watchdogDevice)
                return Watchdog(watchdogPid, watchdogDevice);

            if (opts.DecodeFile is string decodeFile)
                return DecodeCheck(decodeFile);

            if (opts.LatencyFile is string latencyFile)
                return LatencyCheck(opts, latencyFile, render: false);

            if (opts.LatencyRenderFile is string latencyRenderFile)
                return LatencyCheck(opts, latencyRenderFile, render: true);

            if (opts.ListenSeconds is int listenSeconds)
                return ListenCable(listenSeconds);

            if (opts.CheckOutputSeconds is int secondsOut)
                return CheckOutput(secondsOut);

            if (opts.RestoreTo is not null)
            {
                string? target = AudioDevicePolicy.FindCaptureId(opts.RestoreTo);

                if (target is null)
                {
                    Console.WriteLine($"Не найдено устройство ввода со словом «{opts.RestoreTo}».");
                    return 2;
                }

                AudioDevicePolicy.SetDefaultCapture(target);
                Console.WriteLine($"Микрофон по умолчанию → «{opts.RestoreTo}».");
                return 0;
            }

            if (opts.RestoreOnly)
            {
                string? saved = MicTakeover.PreviousDeviceId;
                if (saved is null)
                {
                    Console.WriteLine("Возвращать нечего: сохранённого микрофона нет.");
                    return 0;
                }

                AudioDevicePolicy.SetDefaultCapture(saved);
                Console.WriteLine("Микрофон по умолчанию возвращён.");
                return 0;
            }

            if (opts.ListOnly)
            {
                PrintDevices();
                return 0;
            }

            if (opts.LoopbackFile is not null)
                return LoopbackCheck(opts, opts.LoopbackFile);

            string[] files = LoadPlaylist(opts);

            if (opts.LevelOnly)
                return LevelCheck(opts, files);

            // Микрофон фиксируем до перехвата: после него «по умолчанию» станет наш же кабель.
            string? cableId = AudioDevicePolicy.FindCaptureId("CABLE Output");
            string? physicalMic = AudioDevicePolicy.GetDefaultCaptureId();

            if (cableId is not null && physicalMic == cableId)
            {
                // Прошлый запуск не вернул микрофон (например, убили вместе со сторожем).
                string? saved = MicTakeover.PreviousDeviceId;

                if (saved is not null && saved != cableId)
                {
                    AudioDevicePolicy.SetDefaultCapture(saved);
                    physicalMic = saved;
                    Console.WriteLine("Микрофон по умолчанию восстановлен после прошлого запуска.");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Сейчас микрофон по умолчанию — сам кабель, а сохранённого устройства нет.");
                    Console.WriteLine("Выбери гарнитуру в Windows → Звук → Ввод или укажи --mic \"Graphite\".");
                    Console.ResetColor();
                    return 2;
                }
            }

            Options engineOptions = opts;
            if (opts.Takeover && physicalMic is not null && cableId is not null && physicalMic != cableId)
                engineOptions = opts with { MicDeviceId = opts.MicDeviceId ?? physicalMic };

            using var engine = new MixerEngine(engineOptions.ToAudioSettings());

            engine.PrintBanner();

            if (opts.ProbeOnly)
            {
                Console.WriteLine();
                Console.WriteLine("Устройства открыты, вывод инициализирован. Звук не проигрывался.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine($"Звуки   : {opts.SoundsDir}");

            if (files.Length == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("           пока пусто — положи .wav/.mp3 и перезапусти");
                Console.ResetColor();
            }
            else
            {
                for (int i = 0; i < files.Length; i++)
                    Console.WriteLine($"          F{i + 1} — {Path.GetFileName(files[i])}");
            }

            using var hotkeys = new GlobalHotkeys((key, mods) => HandleHotkey(engine, files, key, mods));

            Console.WriteLine();
            Console.WriteLine("F1..F9 — играть, даже если фокус в Discord");
            Console.WriteLine("F10    — стоп всё");
            Console.WriteLine("F11    — слышать пэды самому (монитор)");
            Console.WriteLine("1..9   — то же, но только когда консоль в фокусе");
            Console.WriteLine("Q      — выход");

            if (!hotkeys.Installed)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Глобальные хоткеи не установились — клавиши только в этом окне.");
                Console.ResetColor();
            }

            Console.WriteLine();

            engine.Start();

            string? acquiredFrom = null;

            if (opts.Takeover && cableId is not null)
            {
                try
                {
                    acquiredFrom = MicTakeover.Acquire(cableId);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Не удалось перехватить микрофон по умолчанию: " + ex.Message);
                    Console.ResetColor();
                }

                if (acquiredFrom is not null)
                {
                    SpawnWatchdog(acquiredFrom);
                    Console.WriteLine();
                    Console.WriteLine("Микрофон по умолчанию → CABLE Output. Discord должен стоять на «Default».");
                }
            }

            if (opts.Demo && files.Length > 0)
            {
                var demo = new Thread(() =>
                {
                    while (true)
                    {
                        Thread.Sleep(1500);
                        try { engine.PlaySound(files[0]); } catch { }
                    }
                })
                { IsBackground = true, Name = "demo" };

                demo.Start();
                Console.WriteLine($"Демо: играю {Path.GetFileName(files[0])} по кругу.");
            }

            try
            {
                RunLoop(engine, files);
            }
            finally
            {
                // Возвращаем голос даже при аварийном выходе.
                MicTakeover.Release(acquiredFrom);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Ошибка: " + ex.Message);
            Console.WriteLine();
            Console.WriteLine(ex);
            Console.ResetColor();
            return 1;
        }
    }

    /// <summary>
    /// Отдельный процесс-сторож: если саундпад убьют или он упадёт, вернуть микрофон
    /// гарнитуры. Без него после жёсткого падения система осталась бы без входа.
    /// </summary>
    private static void SpawnWatchdog(string restoreDeviceId)
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is null)
                return;

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
            // Без сторожа остаётся ручной --restore.
        }
    }

    /// <summary>
    /// Слушает loopback-копию устройства вывода: показывает, звучит ли что-то в наушниках.
    /// Так можно проверить, доходит ли звук до принимающей программы, не слушая ушами.
    /// </summary>
    /// <summary>Проверяет декодирование файла тем же кодом, что и воспроизведение.</summary>
    private static int DecodeCheck(string file)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine("Нет файла: " + file);
            return 2;
        }

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            float[] samples = SoundLoader.LoadSamples(file, 48000, 2);
            clock.Stop();

            double seconds = samples.Length / 2.0 / 48000.0;
            Console.WriteLine($"ок: {samples.Length} сэмплов, {seconds:F2} c, декодировано за {clock.ElapsedMilliseconds} мс");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ошибка декодирования: " + ex.Message);
            Console.WriteLine();
            Console.WriteLine(ex);
            return 3;
        }
    }

    /// <summary>
    /// Меряет задержку: от вызова PlaySound до первых сэмплов на входе кабеля.
    /// Это и есть то, что чувствуется как «пэд сработал с опозданием».
    /// </summary>
    private static int LatencyCheck(Options opts, string file, bool render)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine("Нет файла: " + file);
            return 2;
        }

        using var enumerator = new MMDeviceEnumerator();

        MMDevice? target = render
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

        if (target is null)
        {
            Console.WriteLine("Не найдено устройство для замера.");
            return 2;
        }

        long firstSound = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        using WasapiCapture capture = render
            ? new WasapiLoopbackCapture(target)
            : new WasapiCapture(target);
        capture.DataAvailable += (_, e) =>
        {
            if (Interlocked.Read(ref firstSound) != 0)
                return;

            var samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            for (int i = 0; i < samples.Length; i++)
            {
                if (Math.Abs(samples[i]) <= 0.01)
                    continue;

                Interlocked.CompareExchange(ref firstSound, clock.ElapsedTicks, 0);
                break;
            }
        };

        using var engine = new MixerEngine(opts.ToAudioSettings());
        engine.PrintBanner();
        engine.Start();
        capture.StartRecording();

        // Даём потоку захвата раскрутиться, чтобы не мерить его разгон.
        Thread.Sleep(800);

        clock.Restart();
        engine.PlaySound(file);

        for (int i = 0; i < 500; i++)
        {
            Thread.Sleep(5);
            long ticks = Interlocked.Read(ref firstSound);

            if (ticks == 0)
                continue;

            double milliseconds = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine();
            Console.WriteLine($"Задержка до кабеля: {milliseconds:F0} мс");
            capture.StopRecording();
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Звук не дошёл за 2.5 секунды.");
        capture.StopRecording();
        return 3;
    }

    /// <summary>
    /// Пассивно слушает вход кабеля: показывает, идёт ли туда сигнал.
    /// Ничего не воспроизводит и не занимает микрофон, поэтому безопасен во время работы приложения.
    /// </summary>
    private static int ListenCable(int seconds)
    {
        using var enumerator = new MMDeviceEnumerator();

        MMDevice? cableOut = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

        if (cableOut is null)
        {
            Console.WriteLine("Не найден вход «CABLE Output».");
            return 2;
        }

        var peak = new double[1];

        using var capture = new WasapiCapture(cableOut);
        capture.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            double current = peak[0];
            for (int i = 0; i < samples.Length; i++)
            {
                double value = Math.Abs(samples[i]);
                if (value > current) current = value;
            }

            peak[0] = current;
        };

        capture.StartRecording();

        double max = 0;

        for (int second = 1; second <= seconds; second++)
        {
            Thread.Sleep(1000);
            max = Math.Max(max, peak[0]);
            Console.WriteLine($"  {second} с · пик на входе кабеля {peak[0]:F3}");
            peak[0] = 0;
        }

        capture.StopRecording();

        Console.WriteLine();
        Console.WriteLine(max > 0.001
            ? "В кабель что-то идёт."
            : "В кабеле тишина — источник не пишет.");

        return 0;
    }

    private static int CheckOutput(int seconds)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice render = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        Console.WriteLine($"Слушаю выход: {render.FriendlyName}");

        var peak = new double[1];

        using var loop = new WasapiLoopbackCapture(render);
        loop.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            double current = peak[0];
            for (int i = 0; i < samples.Length; i++)
            {
                double value = Math.Abs(samples[i]);
                if (value > current) current = value;
            }

            peak[0] = current;
        };

        loop.StartRecording();

        double maxPeak = 0;

        for (int second = 1; second <= seconds; second++)
        {
            Thread.Sleep(1000);

            maxPeak = Math.Max(maxPeak, peak[0]);
            Console.WriteLine($"  {second} с · пик {peak[0]:F3}");
            peak[0] = 0;
        }

        loop.StopRecording();

        Console.WriteLine();
        Console.WriteLine(maxPeak > 0.001
            ? "В наушниках что-то звучит."
            : "В наушниках тишина.");

        return 0;
    }

    private static int Watchdog(int pid, string restoreDeviceId)
    {
        try
        {
            using Process target = Process.GetProcessById(pid);
            target.WaitForExit();
        }
        catch
        {
            // Процесс уже завершился — просто возвращаем микрофон.
        }

        AudioDevicePolicy.SetDefaultCapture(restoreDeviceId);
        return 0;
    }

    private static string[] LoadPlaylist(Options opts)
    {
        Directory.CreateDirectory(opts.SoundsDir);

        return Directory
            .GetFiles(opts.SoundsDir)
            .Where(Options.IsAudioFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(9)
            .ToArray();
    }

    /// <summary>Клавиши F1..F9 играют звуки, F10 останавливает всё. Работает без фокуса.</summary>
    private static void HandleHotkey(MixerEngine engine, string[] files, int virtualKey, uint modifiers)
    {
        if (virtualKey is >= 0x70 and <= 0x7A)
            Console.WriteLine($"  [клавиша 0x{virtualKey:X2}]");

        if (virtualKey is >= 0x70 and <= 0x78)
        {
            Play(engine, files, virtualKey - 0x70);
            return;
        }

        if (virtualKey == 0x79)
        {
            engine.StopAll();
            Console.WriteLine("  ■ стоп");
        }
    }

    private static void Play(MixerEngine engine, string[] files, int index)
    {
        if (index < 0 || index >= files.Length)
        {
            Console.WriteLine("  · такого звука нет");
            return;
        }

        string path = files[index];

        _ = Task.Run(() =>
        {
            try
            {
                engine.PlaySound(path);
                Console.WriteLine($"  ▶ {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ! {Path.GetFileName(path)}: {ex.Message}");
            }
        });
    }

    private static void RunLoop(MixerEngine engine, string[] files)
    {
        bool interactive = true;
        try { _ = Console.KeyAvailable; }
        catch { interactive = false; }

        if (!interactive)
        {
            Console.WriteLine("Нет интерактивной консоли — жду Ctrl+C.");
            Thread.Sleep(Timeout.Infinite);
        }

        while (true)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(25);
                continue;
            }

            char key = Console.ReadKey(intercept: true).KeyChar;

            if (key is 'q' or 'Q' or '\u001b')
                break;

            if (key == '0')
            {
                engine.StopAll();
                Console.WriteLine("  ■ стоп");
                continue;
            }

            if (key < '1' || key > '9')
                continue;

            int index = key - '1';
            if (index >= files.Length)
            {
                Console.WriteLine("  · такого звука нет");
                continue;
            }

            string path = files[index];
            _ = Task.Run(() =>
            {
                try
                {
                    engine.PlaySound(path);
                    Console.WriteLine($"  ▶ {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ! {Path.GetFileName(path)}: {ex.Message}");
                }
            });
        }
    }

    /// <summary>
    /// Меряет два уровня сразу: что пришло с микрофона и что дошло до входа кабеля.
    /// Это разделяет «микрофон молчит» и «сигнал не доходит до выхода».
    /// </summary>
    private static int LevelCheck(Options opts, string[] files)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? cableOut = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

        if (cableOut is null)
        {
            Console.WriteLine("Не найден вход «CABLE Output».");
            return 2;
        }

        var cablePeak = new double[1];

        using var capture = new WasapiCapture(cableOut);
        capture.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            double peak = cablePeak[0];
            for (int i = 0; i < samples.Length; i++)
            {
                double value = Math.Abs(samples[i]);
                if (value > peak) peak = value;
            }

            cablePeak[0] = peak;
        };

        using var engine = new MixerEngine(opts.ToAudioSettings());
        engine.PrintBanner();
        Console.WriteLine();
        Console.WriteLine($"Слушаю {opts.Seconds} с.");
        Console.WriteLine("Говори в микрофон и нажимай F1..F9 — покажу уровень голоса и уровень кабеля.");
        Console.WriteLine();

        using var hotkeys = new GlobalHotkeys((key, mods) => HandleHotkey(engine, files, key, mods));
        Console.WriteLine(hotkeys.Installed ? "Хоткеи: глобальный перехват включён." : "Хоткеи: НЕ установились.");

        engine.Start();
        capture.StartRecording();

        double micMax = 0;
        double cableMax = 0;

        for (int second = 1; second <= opts.Seconds; second++)
        {
            Thread.Sleep(1000);

            double mic = engine.MicPeak;
            double cable = cablePeak[0];

            micMax = Math.Max(micMax, mic);
            cableMax = Math.Max(cableMax, cable);

            Console.WriteLine($"  {second,2} с · микрофон {mic:F4} · кабель {cable:F4}");

            engine.ResetMicPeak();
            cablePeak[0] = 0;
        }

        capture.StopRecording();

        Console.WriteLine();
        Console.WriteLine($"Итог: микрофон {micMax:F4} · кабель {cableMax:F4}");
        Console.WriteLine();

        if (micMax < 0.001 && cableMax < 0.001)
            Console.WriteLine("Оба нуля: микрофон молчит и в кабель ничего не идёт.");
        else if (micMax < 0.001)
            Console.WriteLine("Микрофон молчит, но в кабель что-то идёт.");
        else if (cableMax < 0.001)
            Console.WriteLine("Микрофон живой, а до кабеля сигнал не доходит.");
        else
            Console.WriteLine("Оба уровня ненулевые: цепочка работает.");

        return 0;
    }

    private static int LoopbackCheck(Options opts, string file)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine("Нет файла: " + file);
            return 2;
        }

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? cableOut = enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));

        if (cableOut is null)
        {
            Console.WriteLine("Не найден вход «CABLE Output» — кабель установлен не полностью?");
            return 2;
        }

        var peak = new double[1];

        using var capture = new WasapiCapture(cableOut);
        if (capture.WaveFormat.BitsPerSample != 32)
        {
            Console.WriteLine("Неожиданный формат входа кабеля: " + MixerEngine.Describe(capture.WaveFormat));
            return 2;
        }

        capture.DataAvailable += (_, e) =>
        {
            var samples = new float[e.BytesRecorded / 4];
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

            for (int i = 0; i < samples.Length; i++)
            {
                double value = Math.Abs(samples[i]);
                if (value > peak[0])
                    peak[0] = value;
            }
        };

        using var engine = new MixerEngine(opts.ToAudioSettings());
        engine.PrintBanner();
        Console.WriteLine();
        Console.WriteLine($"Играю {Path.GetFileName(file)} и одновременно слушаю вход кабеля…");

        engine.Start();
        capture.StartRecording();
        Thread.Sleep(500);
        engine.PlaySound(file);
        Thread.Sleep(2500);
        capture.StopRecording();

        Console.WriteLine($"Пик на входе кабеля: {peak[0]:F3}");
        Console.WriteLine(peak[0] > 0.01
            ? "OK: звук доходит до кабеля."
            : "ТИШИНА: звук до кабеля не доходит.");

        return peak[0] > 0.01 ? 0 : 2;
    }

    private static void PrintDevices()    {
        using var enumerator = new MMDeviceEnumerator();

        foreach (DataFlow flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            Console.WriteLine(flow == DataFlow.Render ? "== ВЫВОД ==" : "== ВВОД ==");

            string? defaultId = flow == DataFlow.Capture ? AudioDevicePolicy.GetDefaultCaptureId() : null;

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                string format;
                try { format = MixerEngine.Describe(device.AudioClient.MixFormat); }
                catch (Exception ex) { format = "нет доступа: " + ex.Message; }

                string marker = device.ID == defaultId ? "   ← по умолчанию" : "";

                Console.WriteLine($"  {device.FriendlyName}{marker}");
                Console.WriteLine($"     {format}");
            }

            Console.WriteLine();
        }
    }
}
