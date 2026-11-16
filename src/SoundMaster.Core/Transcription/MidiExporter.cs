using NAudio.Midi;

namespace SoundMaster.Core.Transcription;

/// <summary>Ghi các nốt đã bóc ra file MIDI chuẩn (format 1, 480 tick / phách, một track + track tempo).</summary>
public static class MidiExporter
{
    public const int TicksPerQuarter = 480;

    /// <summary>Đổi giây sang tick theo BPM (tempo cố định).</summary>
    public static long SecondsToTicks(double seconds, double bpm)
        => (long)Math.Round(seconds * bpm / 60.0 * TicksPerQuarter);

    public static void Export(string path, IReadOnlyList<TranscribedNote> notes, double bpm, string trackName = "SoundMaster")
    {
        bpm = Math.Clamp(bpm, 20, 400);

        var events = new MidiEventCollection(1, TicksPerQuarter);

        // Track 0: tempo + nhịp 4/4.
        events.AddEvent(new TextEvent("SoundMaster", MetaEventType.SequenceTrackName, 0), 0);
        events.AddEvent(new TempoEvent((int)Math.Round(60_000_000.0 / bpm), 0), 0);
        events.AddEvent(new TimeSignatureEvent(0, 4, 2, 24, 8), 0);

        // Track 1: các nốt.
        events.AddEvent(new TextEvent(trackName, MetaEventType.SequenceTrackName, 0), 1);
        foreach (var note in notes.OrderBy(n => n.StartSeconds))
        {
            long start = SecondsToTicks(note.StartSeconds, bpm);
            long end = Math.Max(start + 1, SecondsToTicks(note.EndSeconds, bpm));
            events.AddEvent(new NoteOnEvent(start, 1, note.MidiNote, note.Velocity, (int)(end - start)), 1);
            events.AddEvent(new NoteEvent(end, 1, MidiCommandCode.NoteOff, note.MidiNote, 0), 1);
        }

        events.PrepareForExport();
        MidiFile.Export(path, events);
    }
}
