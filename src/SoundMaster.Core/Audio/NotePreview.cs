namespace SoundMaster.Core.Audio;

/// <summary>
/// Một giọng đàn đơn giản (sine + vài hài) để nghe thử nốt khi bấm phím piano roll.
/// NoteOn/NoteOff gọi từ UI thread, <see cref="MixInto"/> gọi từ audio thread.
/// </summary>
public sealed class NotePreview
{
    private const float Peak = 0.22f;
    private const double AttackSeconds = 0.006;
    private const double ReleaseSeconds = 0.12;

    private volatile int _note = -1;       // -1 = không phát
    private volatile bool _held;
    private double _phase;
    private float _level;
    private int _renderedNote = -1;

    public void NoteOn(int midiNote)
    {
        _note = Math.Clamp(midiNote, 0, 127);
        _held = true;
    }

    public void NoteOff() => _held = false;

    public bool IsActive => _held || _level > 0.0005f;

    public static double FrequencyOf(int midiNote) => 440.0 * Math.Pow(2, (midiNote - 69) / 12.0);

    /// <summary>Cộng tiếng đàn vào buffer stereo xen kẽ (L,R).</summary>
    public void MixInto(Span<float> buffer)
    {
        int note = _note;
        bool held = _held;
        if (note < 0 || (!held && _level <= 0.0005f))
        {
            _level = 0;
            return;
        }

        if (note != _renderedNote)
        {
            _renderedNote = note;
            _phase = 0;
        }

        double step = 2 * Math.PI * FrequencyOf(note) / EngineFormat.SampleRate;
        float attack = (float)(Peak / (AttackSeconds * EngineFormat.SampleRate));
        float release = (float)(Peak / (ReleaseSeconds * EngineFormat.SampleRate));

        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            _level = held ? Math.Min(Peak, _level + attack) : Math.Max(0, _level - release);
            double p = _phase;
            float v = (float)(Math.Sin(p) + 0.35 * Math.Sin(2 * p) + 0.15 * Math.Sin(3 * p)) * _level;
            buffer[i] += v;
            buffer[i + 1] += v;
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
        }
    }
}
