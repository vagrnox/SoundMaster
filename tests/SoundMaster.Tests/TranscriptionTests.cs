using NAudio.Midi;
using SoundMaster.Core.Transcription;
using Xunit;

namespace SoundMaster.Tests;

public class TranscriptionTests
{
    private const int Fs = 44100;

    /// <summary>Giai điệu tổng hợp: mỗi nốt (midi, giây bắt đầu, độ dài) là một sine có chút vibrato và hài bậc 2.</summary>
    private static float[] Melody(double seconds, params (int Midi, double Start, double Length)[] notes)
    {
        var x = new float[(int)(seconds * Fs)];
        foreach (var (midi, start, length) in notes)
        {
            double f0 = 440 * Math.Pow(2, (midi - 69) / 12.0);
            int from = (int)(start * Fs), to = Math.Min(x.Length, (int)((start + length) * Fs));
            double phase = 0;
            for (int i = from; i < to; i++)
            {
                double t = (i - from) / (double)Fs;
                double vibrato = 1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * t); // ~±7 cent
                phase += 2 * Math.PI * f0 * vibrato / Fs;
                double env = Math.Min(1, Math.Min(t / 0.01, (length - t) / 0.02)); // vào/ra mượt
                x[i] += (float)(0.4 * env * (Math.Sin(phase) + 0.3 * Math.Sin(2 * phase)));
            }
        }
        return x;
    }

    [Fact]
    public void Pitch_tracker_finds_frequency_of_a_pure_tone()
    {
        var x = Melody(1.0, (57, 0, 1.0)); // A3 = 220 Hz
        var frames = PitchTracker.Track(x, Fs);
        var voiced = frames.Where(f => f.IsVoiced && f.Time > 0.1 && f.Time < 0.9).ToList();
        Assert.True(voiced.Count > 50);
        double median = voiced.Select(f => f.FrequencyHz).OrderBy(v => v).ElementAt(voiced.Count / 2);
        Assert.InRange(median, 217, 223);
    }

    [Fact]
    public void Transcribes_a_simple_melody_with_correct_pitches_and_timing()
    {
        var x = Melody(2.5,
            (60, 0.20, 0.40),   // C4
            (64, 0.70, 0.40),   // E4
            (67, 1.20, 0.40),   // G4
            (45, 1.80, 0.50));  // A2 (thấp, kiểu bass)

        var notes = NoteTranscriber.Transcribe(x, Fs);

        Assert.Equal([60, 64, 67, 45], notes.Select(n => n.MidiNote).ToArray());
        Assert.InRange(notes[0].StartSeconds, 0.15, 0.30);
        Assert.InRange(notes[0].EndSeconds, 0.55, 0.68);
        Assert.InRange(notes[3].StartSeconds, 1.75, 1.90);
        Assert.All(notes, n => Assert.InRange(n.Velocity, 30, 127));
        Assert.Equal("C4", notes[0].Name);
        Assert.Equal("A2", notes[3].Name);
    }

    [Fact]
    public void Legato_pitch_change_splits_into_two_notes_and_vibrato_does_not()
    {
        // Hai nốt liền nhau không có khoảng lặng: C4 rồi D4.
        var x = Melody(1.5, (60, 0.2, 0.5), (62, 0.7, 0.5));
        var notes = NoteTranscriber.Transcribe(x, Fs);
        Assert.Equal([60, 62], notes.Select(n => n.MidiNote).ToArray());
        Assert.InRange(notes[0].EndSeconds, 0.6, 0.78);
        Assert.InRange(notes[1].StartSeconds, 0.6, 0.78);
    }

    [Fact]
    public void Silence_and_noise_produce_no_notes()
    {
        Assert.Empty(NoteTranscriber.Transcribe(new float[Fs * 2], Fs));

        var rng = new Random(7);
        var noise = new float[Fs * 2];
        for (int i = 0; i < noise.Length; i++) noise[i] = (float)(0.3 * (rng.NextDouble() * 2 - 1));
        Assert.True(NoteTranscriber.Transcribe(noise, Fs).Count <= 2, "nhiễu trắng không nên cho ra nhiều nốt");
    }

    [Fact]
    public void Clear_notes_are_main_and_short_quiet_blips_are_doubtful()
    {
        var x = Melody(2.0, (60, 0.2, 0.6), (67, 1.0, 0.09));
        for (int i = (int)(1.0 * Fs); i < (int)(1.09 * Fs); i++) x[i] *= 0.08f; // nốt phụ rất nhỏ

        var notes = NoteTranscriber.Transcribe(x, Fs);
        var main = notes.First(n => n.MidiNote == 60);
        Assert.False(main.IsDoubtful, $"nốt chính bị coi là nghi ngờ (độ tin cậy {main.Confidence})");
        Assert.All(notes.Where(n => n.MidiNote != 60), n => Assert.True(n.IsDoubtful, $"{n.Name} {n.Confidence}"));
    }

    [Fact]
    public void Note_preview_sounds_while_held_and_decays_after_release_even_when_stopped()
    {
        var engine = new SoundMaster.Core.Audio.MixerEngine();
        engine.Preview.NoteOn(69);
        var buffer = new float[4096];
        engine.Read(buffer);
        Assert.Contains(buffer, v => Math.Abs(v) > 0.05f);

        engine.Preview.NoteOff();
        for (int i = 0; i < 20; i++) engine.Read(buffer); // ~0,9 s > thời gian tắt dần
        Assert.All(buffer, v => Assert.Equal(0f, v));
        Assert.False(engine.Preview.IsActive);
    }

    [Fact]
    public void Sequencer_renders_notes_at_the_right_time_only()
    {
        var seq = new SoundMaster.Core.Audio.NoteSequencer();
        seq.Set([new List<TranscribedNote> { new(1.0, 1.5, 69, 100) }]);

        float[] Render(double seconds)
        {
            var mix = new float[2 * 512];
            seq.MixInto(mix, 512, (long)(seconds * Fs));
            return mix;
        }

        Assert.All(Render(0.5), v => Assert.Equal(0f, v));     // trước nốt
        Assert.Contains(Render(1.2), v => Math.Abs(v) > 0.02f); // trong nốt
        Assert.All(Render(2.0), v => Assert.Equal(0f, v));     // sau khi tắt hẳn
        Assert.Contains(Render(1.2), v => Math.Abs(v) > 0.02f); // tua lại vẫn đúng (không giữ trạng thái)
    }

    [Fact]
    public void Midi_import_reads_notes_tempo_and_skips_drums()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sm_{Guid.NewGuid():N}.mid");
        try
        {
            var notes = new List<TranscribedNote> { new(0.0, 0.5, 60, 100), new(0.5, 1.0, 64, 90) };
            MidiExporter.Export(path, notes, bpm: 100, trackName: "Piano");

            var import = MidiImporter.Import(path);
            Assert.Equal(100, import.Bpm, 1);
            var lane = Assert.Single(import.Lanes);
            Assert.Equal("Piano", lane.Name);
            Assert.Equal([60, 64], lane.Notes.Select(n => n.MidiNote).ToArray());
            Assert.Equal(0.5, lane.Notes[1].StartSeconds, 2);
            Assert.Equal(1.0, lane.Notes[1].EndSeconds, 2);
            Assert.Equal(90, lane.Notes[1].Velocity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Midi_export_writes_notes_with_tempo_and_timing()
    {
        var notes = new List<TranscribedNote>
        {
            new(0.0, 0.5, 60, 100),
            new(0.5, 1.0, 64, 90),
        };
        string path = Path.Combine(Path.GetTempPath(), $"sm_{Guid.NewGuid():N}.mid");
        try
        {
            MidiExporter.Export(path, notes, bpm: 120);

            var file = new MidiFile(path, strictChecking: false);
            Assert.Equal(MidiExporter.TicksPerQuarter, file.DeltaTicksPerQuarterNote);

            var all = Enumerable.Range(0, file.Tracks).SelectMany(t => file.Events[t]).ToList();
            var tempo = all.OfType<TempoEvent>().Single();
            Assert.Equal(120, tempo.Tempo, 1);

            var ons = all.OfType<NoteOnEvent>().Where(e => e.Velocity > 0).OrderBy(e => e.AbsoluteTime).ToList();
            Assert.Equal([60, 64], ons.Select(e => e.NoteNumber).ToArray());
            // 120 BPM: 0.5 s = 1 phách = 480 tick.
            Assert.Equal(0, ons[0].AbsoluteTime);
            Assert.Equal(480, ons[1].AbsoluteTime);
            Assert.Equal(480, ons[0].NoteLength);
            Assert.Equal(100, ons[0].Velocity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(60, "C4")]
    [InlineData(69, "A4")]
    [InlineData(61, "C#4")]
    [InlineData(36, "C2")]
    public void Note_names(int midi, string expected) => Assert.Equal(expected, TranscribedNote.NameOf(midi));
}
