using System;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ForgePad.Core;

/// <summary>
/// Дуплексный поток: читаем микрофон, подмешиваем звуки, отдаём в кабель.
/// Для системы это и есть «микрофон» — Discord берёт CABLE Output.
/// </summary>
public sealed class MixerEngine : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _micDevice;
    private readonly WasapiCapture _capture;
    private volatile float _micPeak;
    private readonly WaveFormat _micRaw;
    private readonly WaveFormat _outRaw;
    private readonly BufferedWaveProvider _micBuffer;

    /// <summary>
    /// Запас микрофона перед стартом вывода. WASAPI при Play() разом вытягивает примерно
    /// свою латентность, поэтому запас обязан быть больше неё плюс блок захвата.
    /// </summary>
    private const int ReserveMs = 80;

    /// <summary>Выше этого порога буфер подрезаем, чтобы задержка не росла.</summary>
    private const int TrimThresholdMs = 200;

    /// <summary>После подрезки возвращаемся на этот уровень.</summary>
    private const int TrimTargetMs = 80;
    private readonly MixerProvider _mixer;
    private readonly Thread _driftThread;
    private readonly CancellationTokenSource _cts = new();
    private WasapiOut _output;
    private readonly StarvationCounter _starvation;
    private readonly MixerProvider? _monitorMixer;
    private readonly WaveFormat? _monitorRaw;
    private readonly WaveFormat? _monitorFormat;
    private readonly float _monitorGain;
    private WasapiOut? _monitorOut;
    private bool _monitorOn = true;
    private long _trims;
    private long _capturedBytes;
    private long _playedBytes;
    private bool _started;
    private bool _disposed;

    public MixerEngine(AudioSettings opts)
    {
        MMDevice micDevice = ResolveInput(opts);
        MMDevice cableDevice = ResolveOutput(opts);

        _micDevice = micDevice;
        MicName = micDevice.FriendlyName;
        CableName = cableDevice.FriendlyName;

        if (MicName.Contains("CABLE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Микрофоном выбран выход кабеля — это петля и вой. " +
                "Запусти ForgePad.MixerTest.exe --restore и повтори.");

        _capture = new WasapiCapture(micDevice, useEventSync: true, audioBufferMillisecondsLength: opts.CaptureBufferMs);
        _micRaw = _capture.WaveFormat;
        _outRaw = cableDevice.AudioClient.MixFormat;
        MicFormat = Standard(_micRaw);
        OutputFormat = Standard(_outRaw);

        _micBuffer = new BufferedWaveProvider(MicFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(400),
            ReadFully = true,
            DiscardOnBufferOverflow = true,
        };
        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;

        ISampleProvider mic = _micBuffer.ToSampleProvider();
        _starvation = new StarvationCounter(mic, _micBuffer);
        mic = new ChannelAdapter(_starvation, OutputFormat.Channels);

        // Ресемплер включаем только при реальной разнице частот: он добавляет свою задержку.
        if (MicFormat.SampleRate != OutputFormat.SampleRate)
            mic = new WdlResamplingSampleProvider(mic, OutputFormat.SampleRate);

        _mixer = new MixerProvider(mic, OutputFormat) { PadGain = opts.PadGain };

        IWaveProvider chain = BuildChain(
            _mixer, OutputFormat, _outRaw, n => Interlocked.Add(ref _playedBytes, n));

        _output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, true, opts.LatencyMs);
        try
        {
            _output.Init(chain);
        }
        catch
        {
            // Часть виртуальных устройств не умеет event-driven режим — пробуем обычный.
            _output.Dispose();
            _output = new WasapiOut(cableDevice, AudioClientShareMode.Shared, false, opts.LatencyMs);
            _output.Init(chain);
        }

        _monitorGain = opts.MonitorGain;

        if (opts.Monitor)
        {
            MMDevice? speakers = ResolveMonitor(cableDevice);

            if (speakers is not null)
            {
                _monitorRaw = speakers.AudioClient.MixFormat;
                _monitorFormat = Standard(_monitorRaw);

                // Голос в монитор не идёт: слышать себя с задержкой 80 мс невыносимо.
                var silence = new SilenceProvider(_monitorFormat.SampleRate, _monitorFormat.Channels);
                _monitorMixer = new MixerProvider(silence, _monitorFormat) { PadGain = _monitorGain };

                IWaveProvider monitorChain = BuildChain(_monitorMixer, _monitorFormat, _monitorRaw, null);

                _monitorOut = new WasapiOut(speakers, AudioClientShareMode.Shared, true, opts.LatencyMs);
                try
                {
                    _monitorOut.Init(monitorChain);
                }
                catch
                {
                    _monitorOut.Dispose();
                    _monitorOut = new WasapiOut(speakers, AudioClientShareMode.Shared, false, opts.LatencyMs);
                    _monitorOut.Init(monitorChain);
                }

                MonitorName = speakers.FriendlyName;
            }
        }

        _driftThread = new Thread(DriftLoop) { IsBackground = true, Name = "drift" };
    }

    public string MicName { get; }

    public string CableName { get; }

    public WaveFormat MicFormat { get; }

    public WaveFormat OutputFormat { get; }

    /// <summary>Устройство, в которое ты слышишь пэды. null — монитора нет.</summary>
    public string? MonitorName { get; }

    public static string Describe(WaveFormat f) =>
        $"{f.SampleRate} Гц · {f.Channels} кан. · {f.BitsPerSample} бит · {f.Encoding}";

    public void PrintBanner()
    {
        Console.WriteLine($"Микрофон : {MicName}");
        Console.WriteLine($"           {Describe(_micRaw)}");

        try
        {
            AudioEndpointVolume? volume = _micDevice.AudioEndpointVolume;
            if (volume is not null)
                Console.WriteLine($"           mute={volume.Mute} · громкость={volume.MasterVolumeLevelScalar:P0}");
        }
        catch
        {
            // У части устройств нет регулировки громкости — это не ошибка.
        }
        Console.WriteLine($"Кабель   : {CableName}");
        Console.WriteLine($"           {Describe(_outRaw)}");

        if (MonitorName is not null)
            Console.WriteLine($"Монитор  : {MonitorName} — слышишь пэды сам (F11 вкл/выкл)");
        else
            Console.WriteLine("Монитор  : нет устройства для прослушивания");

        if (OutputFormat.SampleRate != MicFormat.SampleRate)
            Console.WriteLine($"           ресемплинг {MicFormat.SampleRate} → {OutputFormat.SampleRate} Гц");
    }

    public void Start()
    {
        _capture.StartRecording();

        // Копим запас: иначе первый же блок вывода прочитает пустой буфер и вставит тишину.
        var fill = System.Diagnostics.Stopwatch.StartNew();
        while (_micBuffer.BufferedDuration < TimeSpan.FromMilliseconds(ReserveMs) &&
               fill.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(5);
        }

        if (_micBuffer.BufferedDuration < TimeSpan.FromMilliseconds(ReserveMs))
            Console.WriteLine("ВНИМАНИЕ: микрофон почти не отдаёт данные, стартую без запаса.");

        _output.Play();
        _monitorOut?.Play();
        _driftThread.Start();
        _started = true;
    }

    public void PlaySound(string path, float gain = 1f)
    {
        float[] samples = SoundLoader.LoadSamples(path, OutputFormat.SampleRate, OutputFormat.Channels);
        _mixer.Add(new Voice { Name = path, Data = samples, Gain = gain });

        if (_monitorMixer is null || _monitorFormat is null)
            return;

        // Форматы могут различаться: тогда декодируем отдельно под монитор.
        float[] monitorSamples =
            _monitorFormat.SampleRate == OutputFormat.SampleRate &&
            _monitorFormat.Channels == OutputFormat.Channels
                ? samples
                : SoundLoader.LoadSamples(path, _monitorFormat.SampleRate, _monitorFormat.Channels);

        _monitorMixer.Add(new Voice { Name = path, Data = monitorSamples, Gain = gain });
    }

    /// <summary>Включает и выключает слышимость пэдов для тебя. Возвращает новое состояние.</summary>
    public bool ToggleMonitor()
    {
        if (_monitorMixer is null)
            return false;

        _monitorOn = !_monitorOn;
        _monitorMixer.PadGain = _monitorOn ? _monitorGain : 0f;

        if (!_monitorOn)
            _monitorMixer.StopAll();

        return _monitorOn;
    }

    /// <summary>Пик микрофона с прошлого сброса — по нему видно, живой ли захват.</summary>
    public double MicPeak => _micPeak;

    public void ResetMicPeak() => _micPeak = 0f;

    public void StopAll() => _mixer.StopAll();

    /// <summary>Что сейчас играет: путь файла и прогресс 0..1 — для подсветки плиток.</summary>
    public IReadOnlyList<(string Name, float Progress)> SnapshotVoices() => _mixer.Snapshot();

    /// <summary>
    /// WASAPI отдаёт mix-формат как Extensible, а построители провайдеров ждут обычный
    /// тег (Float/Pcm). Приводим к стандартному виду, данные при этом не меняются.
    /// </summary>
    private static WaveFormat Standard(WaveFormat format)
    {
        if (format is WaveFormatExtensible extensible)
        {
            try { return extensible.ToStandardWaveFormat(); }
            catch { return format; }
        }

        return format;
    }

    private static IWaveProvider BuildChain(
        ISampleProvider mixer,
        WaveFormat format,
        WaveFormat raw,
        Action<int>? onRead)
    {
        IWaveProvider chain = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => mixer.ToWaveProvider(),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => new SampleToWaveProvider16(mixer),
            _ => throw new InvalidOperationException(
                $"Устройство работает в формате {Describe(raw)}, а прототип умеет 32-bit float и PCM16. " +
                "Смени формат устройства в свойствах Windows на 48 кГц / 32 бит (или 16 бит)."),
        };

        // Предъявляем WASAPI ровно тот заголовок, что она ожидает, не трогая байты.
        if (!format.Equals(raw))
            chain = new FormatWrapper(chain, raw);

        if (onRead is not null)
            chain = new CountingWaveProvider(chain, onRead);

        return chain;
    }

    /// <summary>
    /// Куда играть пэды, чтобы их слышал ты сам. Если «уши» указывают на кабель,
    /// монитор бессмыслен и только даст дубль.
    /// </summary>
    private MMDevice? ResolveMonitor(MMDevice cableDevice)
    {
        try
        {
            MMDevice device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.ID == cableDevice.ID ? null : device;
        }
        catch
        {
            return null;
        }
    }

    private MMDevice ResolveInput(AudioSettings opts)
    {
        // Точный ID важнее всего: после перехвата дефолтом становится наш же кабель,
        // и выбор «по умолчанию» замкнул бы выход на вход.
        if (!string.IsNullOrWhiteSpace(opts.MicDeviceId))
        {
            MMDevice? exact = _enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.ID == opts.MicDeviceId);

            if (exact is not null)
                return exact;
        }

        if (!string.IsNullOrWhiteSpace(opts.MicHint))
        {
            MMDevice? found = _enumerator
                .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains(opts.MicHint!, StringComparison.OrdinalIgnoreCase));

            return found ?? throw new InvalidOperationException(
                $"Не найден микрофон со словом «{opts.MicHint}». Запусти с --list.");
        }

        MMDevice? byDefault = null;

        try { byDefault = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { /* нет устройства по умолчанию — попробуем перебор */ }

        if (byDefault is not null && !IsCable(byDefault))
            return byDefault;

        // «По умолчанию» оказался кабелем (наследие прошлого запуска) — берём живой микрофон.
        MMDevice? fallback = _enumerator
            .EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .FirstOrDefault(d => !IsCable(d));

        return fallback ?? throw new InvalidOperationException(
            "Не нашёл обычного микрофона — только виртуальные кабели. Подключи гарнитуру или укажи --mic.");
    }

    private static bool IsCable(MMDevice device) =>
        device.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase);

    private MMDevice ResolveOutput(AudioSettings opts)
    {
        MMDevice? found = _enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(opts.CableHint!, StringComparison.OrdinalIgnoreCase));

        return found ?? throw new InvalidOperationException(
            $"Не найден выход со словом «{opts.CableHint}». Установи VB-Cable или укажи --cable.");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        Interlocked.Add(ref _capturedBytes, e.BytesRecorded);

        if (MicFormat.BitsPerSample == 32 && MicFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));

            float peak = _micPeak;
            for (int i = 0; i < samples.Length; i++)
            {
                float value = MathF.Abs(samples[i]);
                if (value > peak) peak = value;
            }

            _micPeak = peak;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Console.WriteLine("Захват остановлен: " + e.Exception.Message);
    }

    /// <summary>
    /// Микрофон и кабель тактуются разными кварцами, поэтому буфер медленно уплывает.
    /// Держим задержку в коридоре: излишек подрезаем, недостаток считаем как underrun.
    /// </summary>
    private void DriftLoop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan nextReport = TimeSpan.FromSeconds(3);
        TimeSpan lastSample = TimeSpan.Zero;
        long lastCaptured = 0;
        long lastPlayed = 0;
        long lastStarved = 0;

        int micFrame = MicFormat.Channels * (MicFormat.BitsPerSample / 8);
        int outFrame = _outRaw.Channels * (_outRaw.BitsPerSample / 8);

        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(250);

            TimeSpan buffered = _micBuffer.BufferedDuration;

            if (buffered > TimeSpan.FromMilliseconds(TrimThresholdMs))
            {
                TrimMs((int)buffered.TotalMilliseconds - TrimTargetMs);
                Interlocked.Increment(ref _trims);
                buffered = _micBuffer.BufferedDuration;
            }

            if (clock.Elapsed >= nextReport)
            {
                double seconds = (clock.Elapsed - lastSample).TotalSeconds;
                long captured = Interlocked.Read(ref _capturedBytes);
                long played = Interlocked.Read(ref _playedBytes);
                long starved = _starvation.Count;
                float peakMic = _micPeak;

                double inRate = (captured - lastCaptured) / (double)micFrame / seconds;
                double outRate = (played - lastPlayed) / (double)outFrame / seconds;
                long starvedDelta = starved - lastStarved;

                lastCaptured = captured;
                lastPlayed = played;
                lastStarved = starved;
                lastSample = clock.Elapsed;
                nextReport = clock.Elapsed + TimeSpan.FromSeconds(10);

                Console.WriteLine(
                    $"    [буфер {buffered.TotalMilliseconds:F0} мс · вход {inRate:F0} · выход {outRate:F0} кадр/с · " +
                    $"голос {peakMic:F3} · голод {starvedDelta} · голосов {_mixer.ActiveVoices}]");

                _micPeak = 0f;
            }
        }
    }

    private void TrimMs(int milliseconds)
    {
        if (milliseconds <= 0) return;

        int bytes = _micBuffer.WaveFormat.AverageBytesPerSecond * milliseconds / 1000;
        bytes -= bytes % _micBuffer.WaveFormat.BlockAlign;
        if (bytes <= 0) return;

        var scratch = new byte[bytes];
        _micBuffer.Read(scratch, 0, scratch.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        if (_started)
            _driftThread.Join(TimeSpan.FromSeconds(1));

        try { _capture.StopRecording(); } catch { }
        try { _output.Stop(); } catch { }
        try { _monitorOut?.Stop(); } catch { }

        _capture.Dispose();
        _output.Dispose();
        _monitorOut?.Dispose();
        _enumerator.Dispose();
        _cts.Dispose();
    }
}
