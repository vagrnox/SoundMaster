using SoundMaster.Core.Transcription;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Phát các nốt (MIDI / nốt đã bóc) bằng đàn tổng hợp, đồng bộ với vị trí phát của engine.
/// Không giữ trạng thái giọng: pha được tính thẳng từ thời gian nên tua / lặp vẫn đúng.
/// Danh sách nốt được thay nguyên khối từ UI thread (bất biến), audio thread chỉ đọc.
/// </summary>
public sealed class NoteSequencer
{
    private const double AttackSeconds = 0.005;
    private const double ReleaseSeconds = 0.07;
    private const float Gain = 0.2f;

    private readonly record struct Voice(double Start, double End, double Omega, float Amp);

    private sealed record Snapshot(Voice[] Voices, double MaxLength);

    private volatile Snapshot _snapshot = new([], 0);

    public bool HasNotes => _snapshot.Voices.Length > 0;

    /// <summary>Đặt các nốt cần phát (gộp từ nhiều lane).</summary>
    public void Set(IEnumerable<IReadOnlyList<TranscribedNote>> lanes)
    {
        var voices = new List<Voice>();
        double maxLen = 0;
        foreach (var lane in lanes)
        {
            foreach (var n in lane)
            {
                double f = NotePreview.FrequencyOf(n.MidiNote);
                voices.Add(new Voice(n.StartSeconds, n.EndSeconds, 2 * Math.PI * f, Gain * (0.35f + 0.65f * n.Velocity / 127f)));
                maxLen = Math.Max(maxLen, n.EndSeconds - n.StartSeconds);
            }
        }

        voices.Sort((a, b) => a.Start.CompareTo(b.Start));
        _snapshot = new Snapshot(voices.ToArray(), maxLen);
    }

    public void Clear() => _snapshot = new([], 0);

    /// <summary>Cộng tiếng các nốt đang vang trong khoảng [startFrame, startFrame + frames) vào mix stereo xen kẽ.</summary>
    public void MixInto(float[] mix, int frames, long startFrame)
    {
        var snap = _snapshot;
        var voices = snap.Voices;
        if (voices.Length == 0) return;

        double rate = EngineFormat.SampleRate;
        double t0 = startFrame / rate;
        double t1 = (startFrame + frames) / rate;

        // Nốt đầu tiên có thể còn vang: Start >= t0 - (độ dài lớn nhất + thời gian tắt).
        double earliest = t0 - snap.MaxLength - ReleaseSeconds;
        int lo = 0, hi = voices.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (voices[mid].Start < earliest) lo = mid + 1; else hi = mid;
        }

        for (int i = lo; i < voices.Length && voices[i].Start < t1; i++)
        {
            var v = voices[i];
            if (v.End + ReleaseSeconds <= t0) continue;

            for (int k = 0; k < frames; k++)
            {
                double t = t0 + k / rate;
                double rel = t - v.Start;
                if (rel < 0) continue;

                double env = Math.Min(1.0, rel / AttackSeconds);
                env *= 0.55 + 0.45 * Math.Exp(-rel * 2.5);         // vang tắt dần nhẹ như đàn phím
                if (t > v.End) env *= Math.Max(0.0, 1.0 - (t - v.End) / ReleaseSeconds);
                if (env <= 0) continue;

                double p = v.Omega * rel;
                float s = (float)((Math.Sin(p) + 0.35 * Math.Sin(2 * p) + 0.15 * Math.Sin(3 * p)) * env) * v.Amp;
                mix[2 * k] += s;
                mix[2 * k + 1] += s;
            }
        }
    }
}
