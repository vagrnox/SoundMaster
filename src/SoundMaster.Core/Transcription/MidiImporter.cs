using NAudio.Midi;

namespace SoundMaster.Core.Transcription;

/// <summary>Các nốt của một track (hoặc một kênh) trong file MIDI.</summary>
public sealed record MidiLane(string Name, IReadOnlyList<TranscribedNote> Notes);

public sealed record MidiImport(double Bpm, IReadOnlyList<MidiLane> Lanes);

/// <summary>Đọc file MIDI thành các lane nốt (giây), theo đúng bản đồ tempo; bỏ kênh trống (kênh 10).</summary>
public static class MidiImporter
{
    public static MidiImport Import(string path)
    {
        var file = new MidiFile(path, strictChecking: false);
        int tpq = Math.Max(1, file.DeltaTicksPerQuarterNote);

        // Bản đồ tempo (tick → giây).
        var tempos = new List<(long Tick, double SecondsAtTick, int Micros)>();
        var raw = Enumerable.Range(0, file.Tracks)
            .SelectMany(t => file.Events[t].OfType<TempoEvent>())
            .OrderBy(e => e.AbsoluteTime)
            .ToList();
        int micros = 500_000;
        long lastTick = 0;
        double seconds = 0;
        tempos.Add((0, 0, micros));
        foreach (var t in raw)
        {
            seconds += (t.AbsoluteTime - lastTick) * micros / 1_000_000.0 / tpq;
            lastTick = t.AbsoluteTime;
            micros = t.MicrosecondsPerQuarterNote;
            tempos.Add((lastTick, seconds, micros));
        }

        double ToSeconds(long tick)
        {
            int i = tempos.Count - 1;
            while (i > 0 && tempos[i].Tick > tick) i--;
            var (t0, s0, us) = tempos[i];
            return s0 + (tick - t0) * us / 1_000_000.0 / tpq;
        }

        double bpm = raw.Count > 0 ? 60_000_000.0 / raw[0].MicrosecondsPerQuarterNote : 120;
        string fileName = Path.GetFileNameWithoutExtension(path);
        var lanes = new List<MidiLane>();

        for (int track = 0; track < file.Tracks; track++)
        {
            string trackName = file.Events[track]
                .OfType<TextEvent>()
                .FirstOrDefault(e => e.MetaEventType == MetaEventType.SequenceTrackName)?.Text?.Trim() ?? "";

            var byChannel = new Dictionary<int, List<TranscribedNote>>();
            var open = new Dictionary<(int Channel, int Note), (long Tick, int Velocity)>();

            foreach (var e in file.Events[track])
            {
                if (e is not NoteEvent ne || ne.Channel == 10) continue;
                bool on = e.CommandCode == MidiCommandCode.NoteOn && ne.Velocity > 0;
                bool off = e.CommandCode == MidiCommandCode.NoteOff || (e.CommandCode == MidiCommandCode.NoteOn && ne.Velocity == 0);
                var key = (ne.Channel, ne.NoteNumber);

                if (on)
                {
                    open[key] = (e.AbsoluteTime, ne.Velocity);
                }
                else if (off && open.Remove(key, out var started))
                {
                    double s = ToSeconds(started.Tick), end = ToSeconds(e.AbsoluteTime);
                    if (end - s < 0.01) end = s + 0.01;
                    if (!byChannel.TryGetValue(ne.Channel, out var list)) byChannel[ne.Channel] = list = [];
                    list.Add(new TranscribedNote(s, end, Math.Clamp(ne.NoteNumber, 0, 127), Math.Clamp(started.Velocity, 1, 127)));
                }
            }

            foreach (var (channel, notes) in byChannel.OrderBy(p => p.Key))
            {
                string baseName = trackName.Length > 0 ? trackName : fileName;
                string name = byChannel.Count > 1 ? $"{baseName} ch{channel}" : baseName;
                lanes.Add(new MidiLane(name, notes.OrderBy(n => n.StartSeconds).ToList()));
            }
        }

        return new MidiImport(Math.Clamp(bpm, 30, 300), lanes);
    }
}
