using SoundMaster.Core.Audio;

namespace SoundMaster.Core.Transcription;

/// <summary>Một nốt đã bóc: thời gian (giây), số nốt MIDI (60 = C4) và vận tốc (1..127).</summary>
public sealed record TranscribedNote(double StartSeconds, double EndSeconds, int MidiNote, int Velocity,
    double Confidence = 1.0)
{
    /// <summary>Dưới ngưỡng này nốt bị coi là nghi ngờ (quá ngắn, quá nhỏ, cao độ không ổn định).</summary>
    public const double DoubtfulBelow = 0.5;

    public bool IsDoubtful => Confidence < DoubtfulBelow;

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
        return ScoreNotes(notes, frames);
    }

    /// <summary>
    /// Chấm điểm tin cậy từng nốt: dài (≥ 0,2 s thì đủ điểm), to so với nốt to nhất của track,
    /// và độ chắc chắn cao độ (YIN) trung bình trong nốt. Thấp thì là nốt nghi ngờ (hiện màu đỏ).
    /// </summary>
    private static IReadOnlyList<TranscribedNote> ScoreNotes(List<TranscribedNote> notes, IReadOnlyList<PitchFrame> frames)
    {
        if (notes.Count == 0) return notes;

        var stats = new (double Db, double Conf)[notes.Count];
        for (int i = 0; i < notes.Count; i++)
        {
            double db = 0, conf = 0;
            int n = 0;
            foreach (var f in frames)
            {
                if (f.Time < notes[i].StartSeconds) continue;
                if (f.Time > notes[i].EndSeconds) break;
                if (!f.IsVoiced) continue;
                db += f.RmsDb;
                conf += f.Confidence;
                n++;
            }
            stats[i] = n == 0 ? (-60, 0) : (db / n, conf / n);
        }

        double maxDb = stats.Max(s => s.Db);
        var scored = new List<TranscribedNote>(notes.Count);
        for (int i = 0; i < notes.Count; i++)
        {
            double dur = Math.Clamp((notes[i].Duration - MinNoteSeconds) / (0.20 - MinNoteSeconds), 0, 1);
            double loud = Math.Clamp((stats[i].Db - (maxDb - 26)) / 14.0, 0, 1);
            double pitch = Math.Clamp((stats[i].Conf - 0.55) / 0.35, 0, 1);
            double score = 0.4 * dur + 0.3 * loud + 0.3 * pitch;
            scored.Add(notes[i] with { Confidence = Math.Round(score, 3) });
        }

        return scored;
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
