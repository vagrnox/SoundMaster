namespace SoundMaster.Core.Audio;

/// <summary>
/// Một kênh mixer: fader, pan, mute, solo, đồng hồ đo. Các thuộc tính được UI ghi,
/// audio thread đọc (volatile). Gain/pan được ramp trong mỗi block để không bị tiếng rắc.
/// </summary>
public sealed class ChannelStrip
{
    public const float MinGainDb = -60f;
    public const float MaxGainDb = 6f;

    private volatile float _gainDb;
    private volatile float _pan;
    private volatile bool _muted;
    private volatile bool _soloed;
    private volatile ClipEffect[] _effects = [];

    // Trạng thái chỉ audio thread chạm vào.
    private float _currentLeft;
    private float _currentRight;
    private bool _initialized;

    public ChannelStrip(AudioTrack track)
    {
        Track = track;
        Name = track.Name;
    }

    public AudioTrack Track { get; }
    public string Name { get; set; }
    public PeakMeter Meter { get; } = new();

    public float GainDb
    {
        get => _gainDb;
        set => _gainDb = Math.Clamp(value, MinGainDb, MaxGainDb);
    }

    public float Pan
    {
        get => _pan;
        set => _pan = Math.Clamp(value, -1f, 1f);
    }

    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    public bool Soloed
    {
        get => _soloed;
        set => _soloed = value;
    }

    /// <summary>Thay toàn bộ hiệu ứng theo đoạn của track (an toàn khi đang phát).</summary>
    public void SetEffects(IEnumerable<ClipEffect> effects) => _effects = effects.ToArray();

    public IReadOnlyList<ClipEffect> Effects => _effects;

    internal void ResetState() => _initialized = false;

    /// <summary>Cộng <paramref name="frames"/> frame của stem (từ startFrame) vào bộ đệm mix.</summary>
    internal void MixInto(float[] mix, int frames, long startFrame, bool audible)
    {
        float linear = (_muted || !audible) ? 0f : Decibels.ToLinear(_gainDb, MinGainDb);
        var (panL, panR) = PanLaw.Gains(_pan);
        float targetL = linear * panL;
        float targetR = linear * panR;

        if (!_initialized)
        {
            _currentLeft = targetL;
            _currentRight = targetR;
            _initialized = true;
        }

        float stepL = (targetL - _currentLeft) / frames;
        float stepR = (targetR - _currentRight) / frames;

        var effects = _effects;
        long available = Math.Clamp(Track.FrameCount - startFrame, 0, frames);
        float peakL = 0f, peakR = 0f;

        for (int i = 0; i < frames; i++)
        {
            _currentLeft += stepL;
            _currentRight += stepR;

            float l = 0f, r = 0f;
            if (i < available)
            {
                long pos = startFrame + i;
                if (effects.Length == 0)
                {
                    (l, r) = Track.GetFrame(pos);
                }
                else
                {
                    var (source, clipGain) = ClipEffect.Resolve(effects, pos);
                    if (source >= 0 && source < Track.FrameCount)
                    {
                        (l, r) = Track.GetFrame(source);
                        l *= clipGain;
                        r *= clipGain;
                    }
                }
                l *= _currentLeft;
                r *= _currentRight;
            }

            mix[2 * i] += l;
            mix[2 * i + 1] += r;
            peakL = MathF.Max(peakL, MathF.Abs(l));
            peakR = MathF.Max(peakR, MathF.Abs(r));
        }

        _currentLeft = targetL;
        _currentRight = targetR;
        Meter.Update(peakL, peakR);
    }
}
