using SoundMaster.Core.Audio;

namespace SoundMaster.Core.Transcription;

/// <summary>Một nốt đã bóc: thời gian (giây), số nốt MIDI (60 = C4) và vận tốc (1..127).</summary>
public sealed record TranscribedNote(double StartSeconds, double EndSeconds, int MidiNote, int Velocity)
{
    public double Duration => EndSeconds - StartSeconds;

    public static string NameOf(int midiNote)
    {
        string[] names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        return $"{names[((midiNote % 12) + 12) % 12]}{midiNote / 12 - 1}";
    }

    public string Name => NameOf(MidiNote);
}

/// <summary>
/// Bóc nốt từ một stem đơn âm (giọng hát, bass, lead...): dò cao độ từng khung bằng YIN rồi gom các khung
/// liên tiếp có cao độ gần nhau thành nốt. Chưa dành cho hợp âm / đa âm (đàn piano, pad nhiều nốt).
/// </summary>
public static class NoteTranscriber
{
    /// <summary>Độ lệch tối đa (nửa cung) so với cao độ hiện tại để vẫn coi là cùng nốt (chịu được vibrato).</summary>
    private const double SameNoteTolerance = 0.7;

    /// <summary>Số khung lệch liên tiếp cần có để coi là nốt mới.</summary>
    private const int ChangeFrames = 3;

    /// <summary>Số khung không có cao độ liên tiếp tối đa còn được coi là nốt liền mạch (~35 ms).</summary>
    private const int MaxGapFrames = 3;

    private const double MinNoteSeconds = 0.06;

    public static IReadOnlyList<TranscribedNote> Transcribe(AudioTrack track, CancellationToken ct = default)
    {
        var mono = new float[track.FrameCount];
        const float scale = 1f / 32768f;
        for (int i = 0; i < mono.Length; i++)
            mono[i] = 0.5f * (track.Samples[2 * i] + track.Samples[2 * i + 1]) * scale;
        return Transcribe(mono, EngineFormat.SampleRate, ct);
    }

    public static IReadOnlyList<TranscribedNote> Transcribe(float[] mono, int sampleRate, CancellationToken ct = default)
    {
        var frames = PitchTracker.Track(mono, sampleRate, ct: ct);
        return Segment(frames, PitchTracker.HopSeconds(sampleRate));
    }

    /// <summary>Gom các khung đã dò cao độ thành nốt.</summary>
    public static IReadOnlyList<TranscribedNote> Segment(IReadOnlyList<PitchFrame> frames, double hopSeconds)
    {
        var pitch = new double[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            pitch[i] = frames[i].IsVoiced ? 69 + 12 * Math.Log2(frames[i].FrequencyHz / 440.0) : double.NaN;

        var smooth = MedianFilter(pitch, radius: 2);
        var notes = new List<TranscribedNote>();
        var current = new List<int>();   // chỉ số các khung của nốt đang dựng
        var pending = new List<int>();   // các khung lệch cao độ, chờ xem có phải nốt mới
        int unvoiced = 0;

        void Flush()
        {
            if (current.Count > 0) AddNote(notes, frames, smooth, current, hopSeconds);
            current.Clear();
            pending.Clear();
        }

        for (int i = 0; i < frames.Count; i++)
        {
            if (double.IsNaN(smooth[i]))
            {
                unvoiced++;
                if (unvoiced > MaxGapFrames) Flush();
                continue;
            }
            unvoiced = 0;

            if (current.Count == 0)
            {
                current.Add(i);
                continue;
            }

            double median = Median(current.Select(k => smooth[k]).ToList());
            if (Math.Abs(smooth[i] - median) <= SameNoteTolerance)
            {
                pending.Clear(); // vài khung lệch lẻ tẻ chỉ là nhiễu
                current.Add(i);
                continue;
            }

            pending.Add(i);
            if (pending.Count >= ChangeFrames)
            {
                // Cao độ đã đổi hẳn: đóng nốt cũ, bắt đầu nốt mới từ các khung lệch.
                var next = pending.ToList();
                current.RemoveAll(k => next.Contains(k));
                AddNote(notes, frames, smooth, current, hopSeconds);
                current.Clear();
                current.AddRange(next);
                pending.Clear();
            }
        }

        Flush();
        return notes;
    }

    private static void AddNote(List<TranscribedNote> notes, IReadOnlyList<PitchFrame> frames, double[] smooth,
        List<int> indices, double hop)
    {
        if (indices.Count == 0) return;
        double start = frames[indices[0]].Time - hop / 2;
        double end = frames[indices[^1]].Time + hop / 2;
        if (end - start < MinNoteSeconds) return;

        int midi = (int)Math.Round(Median(indices.Select(k => smooth[k]).ToList()));
        double avgDb = indices.Average(k => frames[k].RmsDb);
        int velocity = (int)Math.Clamp(Math.Round(40 + (avgDb + 40) * 80 / 34), 30, 127);
        notes.Add(new TranscribedNote(Math.Max(0, start), end, Math.Clamp(midi, 0, 127), velocity));
    }

    /// <summary>Lọc trung vị trên các khung có cao độ, bỏ qua khung không có (không bắc cầu qua im lặng).</summary>
    private static double[] MedianFilter(double[] x, int radius)
    {
        var y = new double[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            if (double.IsNaN(x[i]))
            {
                y[i] = double.NaN;
                continue;
            }

            var window = new List<double>(2 * radius + 1);
            for (int k = Math.Max(0, i - radius); k <= Math.Min(x.Length - 1, i + radius); k++)
                if (!double.IsNaN(x[k])) window.Add(x[k]);
            y[i] = Median(window);
        }
        return y;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5 * (values[n / 2 - 1] + values[n / 2]);
    }
}
