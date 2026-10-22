using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
namespace ForgePad.Core;

/// <summary>
/// Приводит число каналов источника к нужному: mono → stereo, stereo → mono и т.п.
/// Свой, а не из NAudio, чтобы не зависеть от версии и покрывать все сочетания.
/// </summary>
internal sealed class ChannelAdapter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _in;
    private readonly int _out;
    private float[] _scratch = Array.Empty<float>();

    public ChannelAdapter(ISampleProvider source, int outChannels)
    {
        _source = source;
        _in = source.WaveFormat.Channels;
        _out = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, outChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_in == _out)
            return _source.Read(buffer, offset, count);

        int frames = count / _out;
        int need = frames * _in;
        if (_scratch.Length < need)
            _scratch = new float[need];

        int got = _source.Read(_scratch, 0, need);
        int gotFrames = got / _in;

        for (int f = 0; f < gotFrames; f++)
        {
            int dst = offset + f * _out;
            int src = f * _in;

            if (_in == 1)
            {
                float s = _scratch[src];
                for (int c = 0; c < _out; c++)
                    buffer[dst + c] = s;
            }
            else if (_out == 1)
            {
                float sum = 0f;
                for (int c = 0; c < _in; c++)
                    sum += _scratch[src + c];
                buffer[dst] = sum / _in;
            }
            else
            {
                for (int c = 0; c < _out; c++)
                    buffer[dst + c] = _scratch[src + (c % _in)];
            }
        }

        return gotFrames * _out;
    }
}

/// <summary>Один играющий звук: предварительно декодированные сэмплы нужного формата.</summary>
internal sealed class Voice
{
    public required string Name { get; init; }
    public required float[] Data { get; init; }
    public float Gain { get; init; } = 1f;
    public int Position { get; set; }

    public bool Finished => Position >= Data.Length;
}

/// <summary>Подмешивает активные звуки к микрофонному потоку.</summary>
internal sealed class MixerProvider : ISampleProvider
{
    private readonly ISampleProvider _mic;
    private readonly object _gate = new();
    private readonly List<Voice> _voices = new();

    public MixerProvider(ISampleProvider mic, WaveFormat outputFormat)
    {
        _mic = mic;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputFormat.SampleRate, outputFormat.Channels);
    }

    public WaveFormat WaveFormat { get; }

    public float PadGain { get; set; } = 1f;

    public int ActiveVoices
    {
        get { lock (_gate) return _voices.Count; }
    }

    public void Add(Voice voice)
    {
        lock (_gate) _voices.Add(voice);
    }

    public void StopAll()
    {
        lock (_gate) _voices.Clear();
    }

    /// <summary>Сбой внутри звукового колбэка: сообщаем наружу, но не роняем процесс.</summary>
    public event Action<Exception>? Fault;

    /// <summary>Снимок играющих звуков: имя (путь файла) и прогресс 0..1.</summary>
    public List<(string Name, float Progress)> Snapshot()
    {
        lock (_gate)
        {
            var result = new List<(string, float)>(_voices.Count);

            foreach (Voice voice in _voices)
            {
                float progress = voice.Data.Length == 0
                    ? 1f
                    : voice.Position / (float)voice.Data.Length;

                result.Add((voice.Name, progress));
            }

            return result;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        try
        {
            int read = _mic.Read(buffer, offset, count);

            lock (_gate)
            {
                for (int i = _voices.Count - 1; i >= 0; i--)
                {
                    Voice voice = _voices[i];
                    float[] data = voice.Data;
                    int n = Math.Min(count, data.Length - voice.Position);

                    if (n <= 0)
                    {
                        // Позиция вышла за данные (не должно случаться) — убираем голос,
                        // иначе получим исключение в аудиопотоке.
                        _voices.RemoveAt(i);
                        continue;
                    }

                    int pos = voice.Position;
                    float gain = voice.Gain * PadGain;

                    for (int k = 0; k < n; k++)
                        buffer[offset + k] += data[pos + k] * gain;

                    voice.Position = pos + n;

                    if (voice.Finished)
                        _voices.RemoveAt(i);
                }
            }

            // Мягкое ограничение: клиппинг жёстко режет, а нам нужен просто потолок.
            for (int i = 0; i < read; i++)
            {
                float s = buffer[offset + i];
                if (s > 1f) buffer[offset + i] = 1f;
                else if (s < -1f) buffer[offset + i] = -1f;
            }

            return read;
        }
        catch (Exception ex)
        {
            // Сбой одного блока не должен убивать приложение: отдаём тишину,
            // чистим голоса и сообщаем наверх.
            Array.Clear(buffer, offset, count);

            lock (_gate)
                _voices.Clear();

            try { Fault?.Invoke(ex); } catch { }

            return count;
        }
    }
}

/// <summary>
/// Отдаёт байты как есть, но объявляет другой заголовок формата.
/// Нужен, чтобы предъявить WASAPI точный mix-формат (Extensible), не конвертируя данные.
/// </summary>
internal sealed class FormatWrapper : IWaveProvider
{
    private readonly IWaveProvider _inner;

    public FormatWrapper(IWaveProvider inner, WaveFormat format)
    {
        _inner = inner;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);
}

/// <summary>
/// Считает, сколько раз вывод запросил данные раньше, чем микрофон их отдал.
/// Это честный показатель провала, в отличие от уровня буфера.
/// </summary>
internal sealed class StarvationCounter : ISampleProvider
{
    private readonly ISampleProvider _inner;
    private readonly BufferedWaveProvider _buffer;
    private long _count;

    public StarvationCounter(ISampleProvider inner, BufferedWaveProvider buffer)
    {
        _inner = inner;
        _buffer = buffer;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public long Count => Interlocked.Read(ref _count);

    public int Read(float[] buffer, int offset, int count)
    {
        int frameBytes = _inner.WaveFormat.Channels * (_inner.WaveFormat.BitsPerSample / 8);
        int needed = count / _inner.WaveFormat.Channels * frameBytes;

        if (_buffer.BufferedBytes < needed)
            Interlocked.Increment(ref _count);

        return _inner.Read(buffer, offset, count);
    }
}

/// <summary>Считает байты, которые реально прочитал вывод — для диагностики расхода.</summary>
internal sealed class CountingWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _inner;
    private readonly Action<int> _onRead;

    public CountingWaveProvider(IWaveProvider inner, Action<int> onRead)
    {
        _inner = inner;
        _onRead = onRead;
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;

    public int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        _onRead(read);
        return read;
    }
}

/// <summary>Постоянная тишина — «микрофонная» заглушка для монитора: голос туда не идёт.</summary>
internal sealed class SilenceProvider : ISampleProvider
{
    public SilenceProvider(int sampleRate, int channels) =>
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}

/// <summary>Декодирует файл целиком в float PCM нужной частоты и числа каналов.</summary>
public static class SoundLoader
{
    /// <summary>
    /// Читает файл двумя путями: Media Foundation (надёжнее для mp3, m4a, flac на Windows 10/11)
    /// и старый AudioFileReader как запасной. Ошибка первого не должна ломать второй.
    /// </summary>
    public static float[] LoadSamples(string path, int sampleRate, int channels)
    {
        Exception? firstFailure = null;

        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();

            using var reader = new MediaFoundationReader(path);
            return Decode(reader.ToSampleProvider(), sampleRate, channels);
        }
        catch (Exception ex)
        {
            firstFailure = ex;
        }

        try
        {
            using var reader = new AudioFileReader(path);
            return Decode(reader, sampleRate, channels);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось прочитать «{Path.GetFileName(path)}». " +
                $"Media Foundation: {firstFailure?.Message ?? "—"}. NAudio: {ex.Message}", ex);
        }
    }

    private static float[] Decode(ISampleProvider source, int sampleRate, int channels)
    {
        ISampleProvider chain = new ChannelAdapter(source, channels);

        if (source.WaveFormat.SampleRate != sampleRate)
            chain = new WdlResamplingSampleProvider(chain, sampleRate);

        var samples = new List<float>(sampleRate * channels);
        var frame = new float[channels * 2048];
        int n;

        while ((n = chain.Read(frame, 0, frame.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
                samples.Add(frame[i]);
        }

        return samples.ToArray();
    }
}
