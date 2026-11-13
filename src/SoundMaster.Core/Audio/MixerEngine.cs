using NAudio.Wave;

namespace SoundMaster.Core.Audio;

public enum ExportFormat { Pcm16, Pcm24, Float32 }

public readonly record struct ExportResult(long Frames, float PeakDbfs);

/// <summary>
/// Bộ mixer: cộng các <see cref="ChannelStrip"/> → master gain → limiter → ra thiết bị.
/// <see cref="Read"/> chạy trên audio thread nên không cấp phát và không chờ khóa.
/// </summary>
public sealed class MixerEngine : ISampleProvider
{
    private const int SubBlockFrames = 512;
    public const float MasterMinDb = -60f;
    public const float MasterMaxDb = 6f;

    private readonly object _renderLock = new();
    private readonly float[] _mix = new float[SubBlockFrames * 2];

    private volatile ChannelStrip[] _strips = [];
    private long _position;
    private long _lengthFrames;
    private volatile bool _playing;
    private volatile float _masterGainDb;

    private float _masterCurrent;
    private bool _masterInitialized;

    public WaveFormat WaveFormat { get; } =
        WaveFormat.CreateIeeeFloatWaveFormat(EngineFormat.SampleRate, EngineFormat.Channels);

    public IReadOnlyList<ChannelStrip> Strips => _strips;
    public SafetyLimiter Limiter { get; } = new();
    public PeakMeter MasterMeter { get; } = new();
    public bool Loop { get; set; }

    private volatile LoopRegion? _loopRegion;

    /// <summary>Đoạn đang lặp (null = không lặp đoạn). Khác với <see cref="Loop"/> lặp cả bài.</summary>
    public LoopRegion? LoopRegion => _loopRegion;

    /// <summary>Lặp đoạn [start, end) khi phát tới cuối đoạn thì quay lại đầu đoạn.</summary>
    public void SetLoopRegion(long startFrame, long endFrame)
    {
        startFrame = Math.Max(0, startFrame);
        endFrame = Math.Min(endFrame, Math.Max(LengthFrames, endFrame));
        _loopRegion = endFrame > startFrame ? new LoopRegion(startFrame, endFrame) : null;
    }

    public void ClearLoopRegion() => _loopRegion = null;
    public bool IsPlaying => _playing;
    public long LengthFrames => Interlocked.Read(ref _lengthFrames);
    public long PositionFrames => Interlocked.Read(ref _position);

    public float MasterGainDb
    {
        get => _masterGainDb;
        set => _masterGainDb = Math.Clamp(value, MasterMinDb, MasterMaxDb);
    }

    public static double FramesToSeconds(long frames) => (double)frames / EngineFormat.SampleRate;
    public static long SecondsToFrames(double seconds) => (long)(seconds * EngineFormat.SampleRate);

    public void SetStrips(IEnumerable<ChannelStrip> strips)
    {
        var array = strips.ToArray();
        Interlocked.Exchange(ref _lengthFrames, array.Length == 0 ? 0 : array.Max(s => (long)s.Track.FrameCount));
        _strips = array;
        if (PositionFrames > LengthFrames) Seek(0);
    }

    public void Play()
    {
        if (LengthFrames == 0) return;
        if (PositionFrames >= LengthFrames) Seek(0);
        _playing = true;
    }

    public void Pause() => _playing = false;

    public void Stop()
    {
        _playing = false;
        Seek(0);
    }

    public void Seek(long frame)
        => Interlocked.Exchange(ref _position, Math.Clamp(frame, 0, LengthFrames));

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        if (!_playing || !Monitor.TryEnter(_renderLock))
        {
            buffer.Clear();
            return buffer.Length;
        }

        try
        {
            int frames = buffer.Length / 2;
            long start = Interlocked.Read(ref _position);
            long pos = start;
            int done = 0;

            while (done < frames)
            {
                var region = _loopRegion;
                if (region is not null && pos == region.End) pos = region.Start; // hết đoạn lặp: quay về đầu đoạn

                if (pos >= LengthFrames)
                {
                    if (Loop && LengthFrames > 0)
                    {
                        pos = 0;
                    }
                    else
                    {
                        _playing = false;
                        break;
                    }
                }

                int n = (int)Math.Min(Math.Min(SubBlockFrames, frames - done), LengthFrames - pos);
                if (region is not null && pos < region.End) n = (int)Math.Min(n, region.End - pos);
                RenderBlock(buffer.Slice(done * 2, n * 2), n, pos);
                pos += n;
                done += n;
            }

            if (done < frames)
                buffer.Slice(done * 2, (frames - done) * 2).Clear();

            // Nếu UI vừa Seek trong lúc render thì giữ vị trí mới.
            Interlocked.CompareExchange(ref _position, pos, start);
            return buffer.Length;
        }
        finally
        {
            Monitor.Exit(_renderLock);
        }
    }

    /// <summary>
    /// Render offline toàn bộ mix ra file WAV. Chiếm khóa render nên audio thread sẽ phát im lặng
    /// trong lúc xuất; nên gọi <see cref="Pause"/> trước.
    /// </summary>
    public ExportResult ExportWav(
        string path,
        ExportFormat format = ExportFormat.Pcm24,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (LengthFrames == 0)
            throw new InvalidOperationException("Chưa có stem nào để xuất.");

        lock (_renderLock)
        {
            ResetDspState();
            try
            {
                var waveFormat = format switch
                {
                    ExportFormat.Pcm16 => new WaveFormat(EngineFormat.SampleRate, 16, 2),
                    ExportFormat.Pcm24 => new WaveFormat(EngineFormat.SampleRate, 24, 2),
                    _ => WaveFormat.CreateIeeeFloatWaveFormat(EngineFormat.SampleRate, 2),
                };

                var block = new float[SubBlockFrames * 2];
                var bytes = new byte[SubBlockFrames * 2 * 4];
                float peak = 0f;
                long total = LengthFrames;

                using var writer = new WaveFileWriter(path, waveFormat);
                for (long pos = 0; pos < total;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(SubBlockFrames, total - pos);
                    RenderBlock(block.AsSpan(0, n * 2), n, pos);

                    for (int i = 0; i < n * 2; i++)
                        peak = MathF.Max(peak, MathF.Abs(block[i]));

                    int byteCount = EncodeSamples(block, n * 2, bytes, format);
                    writer.Write(bytes, 0, byteCount);

                    pos += n;
                    progress?.Report((double)pos / total);
                }

                return new ExportResult(total, Decibels.FromLinear(peak));
            }
            finally
            {
                ResetDspState();
            }
        }
    }

    private void ResetDspState()
    {
        foreach (var s in _strips) s.ResetState();
        Limiter.Reset();
        _masterInitialized = false;
        foreach (var s in _strips) s.Meter.ReadAndReset();
        MasterMeter.ReadAndReset();
    }

    /// <summary>Render một sub-block (≤ 512 frame) từ vị trí <paramref name="pos"/> vào <paramref name="dest"/>.</summary>
    private void RenderBlock(Span<float> dest, int frames, long pos)
    {
        Array.Clear(_mix, 0, frames * 2);

        var strips = _strips;
        bool anySolo = false;
        foreach (var s in strips)
        {
            if (s.Soloed) { anySolo = true; break; }
        }

        foreach (var s in strips)
            s.MixInto(_mix, frames, pos, audible: !anySolo || s.Soloed);

        float target = Decibels.ToLinear(_masterGainDb, MasterMinDb);
        if (!_masterInitialized)
        {
            _masterCurrent = target;
            _masterInitialized = true;
        }

        float step = (target - _masterCurrent) / frames;
        for (int i = 0; i < frames; i++)
        {
            _masterCurrent += step;
            _mix[2 * i] *= _masterCurrent;
            _mix[2 * i + 1] *= _masterCurrent;
        }

        _masterCurrent = target;

        Limiter.Process(_mix, frames);

        float peakL = 0f, peakR = 0f;
        for (int i = 0; i < frames; i++)
        {
            peakL = MathF.Max(peakL, MathF.Abs(_mix[2 * i]));
            peakR = MathF.Max(peakR, MathF.Abs(_mix[2 * i + 1]));
        }

        MasterMeter.Update(peakL, peakR);
        _mix.AsSpan(0, frames * 2).CopyTo(dest);
    }

    private static int EncodeSamples(float[] samples, int count, byte[] bytes, ExportFormat format)
    {
        int o = 0;
        switch (format)
        {
            case ExportFormat.Pcm16:
                for (int i = 0; i < count; i++)
                {
                    // Dither TPDF ±1 LSB trước khi cắt xuống 16-bit.
                    float dither = (Random.Shared.NextSingle() - Random.Shared.NextSingle()) / 32768f;
                    int v = Math.Clamp((int)MathF.Round((samples[i] + dither) * 32767f), short.MinValue, short.MaxValue);
                    bytes[o++] = (byte)v;
                    bytes[o++] = (byte)(v >> 8);
                }
                break;
            case ExportFormat.Pcm24:
                for (int i = 0; i < count; i++)
                {
                    int v = Math.Clamp((int)MathF.Round(samples[i] * 8388607f), -8388608, 8388607);
                    bytes[o++] = (byte)v;
                    bytes[o++] = (byte)(v >> 8);
                    bytes[o++] = (byte)(v >> 16);
                }
                break;
            default:
                Buffer.BlockCopy(samples, 0, bytes, 0, count * 4);
                o = count * 4;
                break;
        }

        return o;
    }
}

/// <summary>Đoạn [Start, End) của bài được phát lặp.</summary>
public sealed record LoopRegion(long Start, long End);
