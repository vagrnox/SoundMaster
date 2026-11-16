using System.Xml.Linq;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SoundMaster.App.ViewModels;
using SoundMaster.App.Views;
using SoundMaster.Core.Notation;
using SoundMaster.Core.Transcription;
using Xunit;

namespace SoundMaster.Tests;

public class NotationTests
{
    // 120 BPM: một phách = 0,5 s; một ô (4 phách) = 2 s.
    private static List<TranscribedNote> Melody() =>
    [
        new(0.0, 0.5, 64, 90), new(0.5, 1.0, 66, 90), new(1.0, 1.5, 67, 90), new(1.5, 3.0, 69, 90), // nốt cuối vắt qua vạch nhịp
        new(0.0, 2.0, 48, 80),                                                                         // bass nốt tròn... 4 phách
    ];

    [Fact]
    public void Every_measure_of_each_staff_is_completely_filled()
    {
        var score = ScoreBuilder.Build(Melody(), 120);
        Assert.Equal(2, score.Measures);
        foreach (var staff in new[] { score.Treble, score.Bass })
        {
            for (int m = 0; m < score.Measures; m++)
                Assert.Equal(16, staff.Where(e => e.Measure == m).Sum(e => e.Duration16));
        }
    }

    [Fact]
    public void Notes_are_split_at_the_barline_and_tied()
    {
        var score = ScoreBuilder.Build(Melody(), 120);
        var tied = score.Treble.Where(e => e.Midis.SequenceEqual([69])).ToList();
        Assert.Equal(2, tied.Count);
        Assert.Equal(0, tied[0].Measure);
        Assert.Equal(12, tied[0].Start16);
        Assert.Equal(4, tied[0].Duration16);
        Assert.True(tied[0].TieToNext);
        Assert.Equal(1, tied[1].Measure);
        Assert.True(tied[1].TieFromPrevious);
        Assert.Equal(8, tied[1].Duration16);
    }

    [Fact]
    public void Low_notes_go_to_the_bass_staff_and_rests_fill_gaps()
    {
        var score = ScoreBuilder.Build(Melody(), 120);
        Assert.Contains(score.Bass, e => e.Midis.SequenceEqual([48]));
        Assert.DoesNotContain(score.Treble, e => e.Midis.Contains(48));
        // Hết bass ở ô 2 → dấu lặng tròn.
        Assert.Contains(score.Bass, e => e.Measure == 1 && e.IsRest && e.Duration16 == 16);
    }

    [Fact]
    public void Simultaneous_notes_become_a_chord()
    {
        var score = ScoreBuilder.Build([new(0, 1, 60, 90), new(0, 1, 64, 90), new(0, 1, 67, 90)], 120);
        var chord = Assert.Single(score.Treble, e => !e.IsRest);
        Assert.Equal([60, 64, 67], chord.Midis);
        Assert.Equal(8, chord.Duration16);
    }

    [Fact]
    public void Lyrics_parse_syllables_hyphens_and_skips()
    {
        var parts = Lyrics.Parse("Hel-lo wor-ld _ yes");
        Assert.Equal(6, parts.Count);
        Assert.Equal("single", parts[0]!.Syllabic == "begin" ? "single" : "x"); // "Hel" mở đầu từ
        Assert.Equal(["begin", "end", "begin", "end"], new[] { parts[0], parts[1], parts[2], parts[3] }.Select(p => p!.Syllabic).ToArray());
        Assert.Null(parts[4]);
        Assert.Equal("single", parts[5]!.Syllabic);
        Assert.Equal("Hel-", parts[0]!.Display);
        Assert.Equal("lo", parts[1]!.Display);
    }

    [Fact]
    public void Lyrics_are_assigned_per_onset_skipping_ties_and_underscores()
    {
        var score = ScoreBuilder.Build(Melody(), 120);
        // Chỗ có lời: t=0 (treble 64 + bass 48 cùng lúc → một chỗ), 0.5, 1.0, 1.5 → 4 chỗ (nốt nối ở ô 2 không tính).
        Assert.Equal(4, score.Slots.Count);

        int assigned = score.AssignLyrics(Lyrics.Parse("la _ la lai"));
        Assert.Equal(3, assigned);
        Assert.Equal("la", score.Treble.First(e => e.Midis.SequenceEqual([64])).Lyric!.Text);
        Assert.Null(score.Treble.First(e => e.Midis.SequenceEqual([66])).Lyric);
        Assert.Equal("lai", score.Treble.First(e => e.Midis.SequenceEqual([69])).Lyric!.Text);
    }

    [Fact]
    public void MusicXml_is_wellformed_with_two_staves_ties_and_lyrics()
    {
        var score = ScoreBuilder.Build(Melody(), 120);
        score.AssignLyrics(Lyrics.Parse("Mot hai ba bon"));
        string xml = MusicXmlExporter.ToXml(score, "Test");

        var doc = XDocument.Parse(xml.Replace("<!DOCTYPE", "<!--").Replace("\"http://www.musicxml.org/dtds/partwise.dtd\">", "-->"));
        var part = doc.Root!.Element("part")!;
        var measures = part.Elements("measure").ToList();
        Assert.Equal(2, measures.Count);
        Assert.Equal("2", measures[0].Element("attributes")!.Element("staves")!.Value);

        // Mỗi ô: tổng duration (trừ nốt chord) mỗi khuông = 16.
        foreach (var m in measures)
        {
            var notes = m.Elements("note").Where(n => n.Element("chord") is null).ToList();
            foreach (var staff in new[] { "1", "2" })
                Assert.Equal(16, notes.Where(n => n.Element("staff")!.Value == staff).Sum(n => int.Parse(n.Element("duration")!.Value)));
        }

        Assert.Equal(["Mot", "hai", "ba", "bon"], doc.Descendants("lyric").Select(l => l.Element("text")!.Value).ToArray());
        Assert.Equal(2, doc.Descendants("tie").Count()); // start + stop
        Assert.Contains(doc.Descendants("pitch"), p => p.Element("step")!.Value == "F" && p.Element("alter")?.Value == "1");
    }

    [Fact]
    public void Midi_export_includes_lyrics_as_meta_events()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sm_{Guid.NewGuid():N}.mid");
        try
        {
            MidiExporter.Export(path, Melody(), 120, "T", "a b - c");
            var file = new NAudio.Midi.MidiFile(path, false);
            var lyrics = Enumerable.Range(0, file.Tracks).SelectMany(t => file.Events[t])
                .OfType<NAudio.Midi.TextEvent>().Where(t => t.MetaEventType == NAudio.Midi.MetaEventType.Lyric)
                .Select(t => t.Text).ToList();
            Assert.Equal(["a", "b", "c"], lyrics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task Settle(Task task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
        await task;
    }

    [AvaloniaFact]
    public async Task Sheet_view_builds_score_with_lyrics_exports_and_roundtrips_lyrics()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sm_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var vm = new MainViewModel(enableAudioOutput: false);
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800, ConfirmOnClose = false };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            string midi = Path.Combine(dir, "tune.mid");
            var notes = new List<TranscribedNote>();
            int[] pitches = [60, 62, 64, 65, 67, 65, 64, 62, 60, 64, 67, 72, 71, 67, 64, 60, 55, 59, 62, 67, 66, 64, 62, 60];
            for (int i = 0; i < pitches.Length; i++)
                notes.Add(new TranscribedNote(i * 0.5, i * 0.5 + (i % 4 == 3 ? 0.9 : 0.45), pitches[i], 90));
            notes.Add(new(0, 2, 48, 80));
            notes.Add(new(2, 4, 43, 80));
            MidiExporter.Export(midi, notes, 120, "Giai dieu");

            await Settle(vm.ImportMidiAsync(midi, play: false));
            vm.ShowSheetCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.IsSheet);
            Assert.NotNull(vm.SheetScore);
            var track = vm.SelectedTrack!;
            track.Lyrics = "Ta la ta lu ta lo ben nhau cung hat vang xa nhu gio bay-ve troi _ xanh";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("âm tiết", vm.LyricsInfo);
            Assert.Contains(vm.SheetScore!.All, e => e.Lyric?.Text == "Ta");

            var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(Path.GetTempPath(), "soundmaster_sheet.png"));

            // Xuất ra file.
            string xml = Path.Combine(dir, "tune.musicxml");
            vm.ExportMusicXml(xml);
            Assert.True(File.Exists(xml));
            Assert.Contains("<lyric", File.ReadAllText(xml));

            var sheet = window.GetVisualDescendantsOfType<SheetView>();
            string png = Path.Combine(dir, "sheet.png");
            sheet.SavePng(png);
            Assert.True(new FileInfo(png).Length > 3000);

            // Lời được lưu cùng project.
            string proj = Path.Combine(dir, "p.smx");
            vm.SaveProject(proj);
            var vm2 = new MainViewModel(enableAudioOutput: false);
            Assert.True(await OpenAsync(vm2, proj));
            Assert.StartsWith("Ta la ta", vm2.Tracks[0].Lyrics);

            vm2.Dispose();
            window.Close();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static async Task<bool> OpenAsync(MainViewModel vm, string path)
    {
        var task = vm.OpenProjectAsync(path);
        await Settle(task);
        return task.Result;
    }
}

internal static class VisualExt
{
    public static T GetVisualDescendantsOfType<T>(this Avalonia.Visual root) where T : class
        => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().First();
}
