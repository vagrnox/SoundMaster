using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NAudio.Wave;
using SoundMaster.App.ViewModels;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Project;
using Xunit;

namespace SoundMaster.Tests;

/// <summary>Lưu/mở project, undo/redo và tự điền BPM ở mức MainViewModel (không mở thiết bị âm thanh).</summary>
public class ProjectViewModelTests
{
    private static MainViewModel NewVm() => new(enableAudioOutput: false);

    private static void Pump(Func<bool> until, int timeoutMs = 15000)
    {
        for (int waited = 0; waited < timeoutMs && !until(); waited += 20)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm_vm_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Ghi file WAV stereo 16-bit gồm các tiếng "kick" đều nhịp ở <paramref name="bpm"/>.</summary>
    private static string WriteClickWav(string dir, string name, double bpm, double seconds)
    {
        const int sr = 44100;
        int frames = (int)(seconds * sr);
        var data = new float[frames * 2];
        for (double t = 0.1; t < seconds - 0.2; t += 60.0 / bpm)
        {
            int s = (int)(t * sr);
            for (int i = 0; i < 4000 && s + i < frames; i++)
            {
                float v = (float)(0.8 * Math.Exp(-i / 800.0) * Math.Sin(2 * Math.PI * 80 * i / sr));
                data[2 * (s + i)] += v;
                data[2 * (s + i) + 1] += v;
            }
        }

        string path = Path.Combine(dir, name + ".wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(sr, 16, 2));
        writer.WriteSamples(data, 0, data.Length);
        return path;
    }

    [AvaloniaFact]
    public void Undo_and_redo_walk_through_edits_and_structure()
    {
        var vm = NewVm();
        vm.LoadDemoCommand.Execute(null);
        vm.CommitPendingEdits();
        Assert.Equal(5, vm.Tracks.Count);

        vm.Tracks[0].GainDb = -9;
        vm.Tracks[1].IsMuted = true;
        vm.CommitPendingEdits();
        vm.Tracks[2].Pan = 0.6;
        vm.CommitPendingEdits();
        Assert.True(vm.CanUndo);
        Assert.False(vm.CanRedo);

        vm.UndoCommand.Execute(null);                 // bỏ pan
        Assert.Equal(0, vm.Tracks[2].Pan);
        Assert.Equal(-9, vm.Tracks[0].GainDb);
        Assert.True(vm.CanRedo);

        vm.UndoCommand.Execute(null);                 // bỏ volume + mute (cùng một bước)
        Assert.Equal(0, vm.Tracks[0].GainDb);
        Assert.False(vm.Tracks[1].IsMuted);

        vm.RedoCommand.Execute(null);
        Assert.Equal(-9, vm.Tracks[0].GainDb);
        Assert.True(vm.Tracks[1].IsMuted);
        vm.RedoCommand.Execute(null);
        Assert.Equal(0.6, vm.Tracks[2].Pan, 3);
        Assert.False(vm.CanRedo);

        // Xóa track rồi hoàn tác: track quay lại đúng chỗ với cài đặt cũ.
        var victim = vm.Tracks[1];
        string victimName = victim.Name;
        vm.RemoveTrackCommand.Execute(victim);
        vm.CommitPendingEdits();
        Assert.Equal(4, vm.Tracks.Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(5, vm.Tracks.Count);
        Assert.Equal(victimName, vm.Tracks[1].Name);
        Assert.True(vm.Tracks[1].IsMuted);
        Assert.Equal([1, 2, 3, 4, 5], vm.Tracks.Select(t => t.Index).ToArray());
        Assert.Equal(-9, vm.Tracks[0].GainDb);

        // Đổi thứ tự rồi hoàn tác.
        var order = vm.Tracks.Select(t => t.Name).ToArray();
        vm.MoveTrack(0, 3);
        vm.CommitPendingEdits();
        vm.UndoCommand.Execute(null);
        Assert.Equal(order, vm.Tracks.Select(t => t.Name).ToArray());

        // Cuối cùng lùi hết về project trống.
        for (int i = 0; i < 20 && vm.CanUndo; i++) vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Tracks);
        Assert.False(vm.CanUndo);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Undo_restores_regions_effects_and_loop()
    {
        var vm = NewVm();
        vm.LoadDemoCommand.Execute(null);
        vm.CommitPendingEdits();

        var t = vm.Tracks[0];
        var r = t.BeginRegion(2, 5);
        t.SetEffect(r, RegionEffect.Loop);
        vm.CommitPendingEdits();
        Assert.NotNull(vm.ActiveLoop);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Tracks[0].Regions);
        Assert.Null(vm.ActiveLoop);

        vm.RedoCommand.Execute(null);
        var back = Assert.Single(vm.Tracks[0].Regions);
        Assert.Equal(RegionEffect.Loop, back.Effect);
        Assert.Equal(2, back.StartSeconds, 3);
        Assert.NotNull(vm.ActiveLoop);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Dirty_flag_follows_saves_and_edits()
    {
        string dir = TempDir();
        try
        {
            var vm = NewVm();
            vm.LoadDemoCommand.Execute(null);
            vm.PollHistory(force: true);
            Assert.True(vm.IsDirty);                 // chưa lưu bao giờ nhưng đã có nội dung

            string path = Path.Combine(dir, "song.smx");
            vm.SaveProject(path);
            Assert.False(vm.IsDirty);
            Assert.Equal(path, vm.ProjectPath);
            Assert.Contains("song", vm.WindowTitle);
            Assert.DoesNotContain("•", vm.WindowTitle);

            vm.Tracks[0].GainDb = -4;
            vm.PollHistory(force: true);
            Assert.True(vm.IsDirty);
            Assert.Contains("•", vm.WindowTitle);

            vm.Tracks[0].GainDb = 0;                 // trả về như lúc lưu
            vm.PollHistory(force: true);
            Assert.False(vm.IsDirty);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Save_then_open_restores_files_settings_and_regions()
    {
        string dir = TempDir();
        try
        {
            string kick = WriteClickWav(dir, "kick", 120, 4);
            string bass = WriteClickWav(dir, "bass", 90, 4);
            var vm = NewVm();
            await vm.LoadFilesAsync([kick, bass], replace: true);
            Assert.Equal(2, vm.Tracks.Count);

            vm.SongName = "My Song";
            vm.Timeline.Bpm = 97;
            vm.MasterGainDb = -3;
            vm.LimiterEnabled = false;
            vm.SpeedFactor = 0.8;
            vm.Tracks[0].Name = "Kick drum";
            vm.Tracks[0].ColorHex = "#2196F3";
            vm.Tracks[0].GainDb = -6;
            vm.Tracks[0].Pan = -0.4;
            vm.Tracks[1].IsMuted = true;
            vm.Tracks[1].IsSolo = true;
            vm.Tracks[1].IsLocked = true;
            var region = vm.Tracks[0].BeginRegion(0.5, 2.5);
            vm.Tracks[0].SetEffect(region, RegionEffect.FadeIn);

            string path = Path.Combine(dir, "song.smx");
            vm.SaveProject(path);

            // Đường dẫn file gốc được lưu tương đối (cùng thư mục).
            var doc = ProjectFile.Load(path);
            Assert.Equal("kick.wav", doc.Tracks[0].Source);

            vm.NewProject();
            Assert.Empty(vm.Tracks);
            Assert.Equal("Untitled", vm.SongName);

            await vm.OpenProjectAsync(path);
            Assert.Equal(2, vm.Tracks.Count);
            Assert.Equal("My Song", vm.SongName);
            Assert.Equal(97, vm.Timeline.Bpm);
            Assert.Equal(-3, vm.MasterGainDb);
            Assert.False(vm.LimiterEnabled);
            Assert.Equal(0.8, vm.SpeedFactor, 3);
            Assert.Equal("Kick drum", vm.Tracks[0].Name);
            Assert.Equal("#2196F3", vm.Tracks[0].ColorHex);
            Assert.Equal(-6, vm.Tracks[0].GainDb);
            Assert.Equal(-0.4, vm.Tracks[0].Pan, 3);
            Assert.True(vm.Tracks[1].IsMuted);
            Assert.True(vm.Tracks[1].IsSolo);
            Assert.True(vm.Tracks[1].IsLocked);
            var back = Assert.Single(vm.Tracks[0].Regions);
            Assert.Equal(RegionEffect.FadeIn, back.Effect);
            Assert.Equal(2.5, back.EndSeconds, 3);
            Assert.Equal(ClipEffectKind.FadeIn, Assert.Single(vm.Tracks[0].Strip.Effects).Kind);
            Assert.False(vm.IsDirty);
            Assert.False(vm.CanUndo);                  // mở project xóa lịch sử cũ

            // Mở thẳng project có file thiếu: các track còn lại vẫn nạp, báo rõ cái nào thiếu, giữ trạng thái chưa lưu.
            File.Delete(bass);
            await vm.OpenProjectAsync(path);
            Assert.Single(vm.Tracks);
            Assert.Contains("thiếu 1 track", vm.Status);
            Assert.Contains("bass", vm.Status);
            Assert.True(vm.IsDirty);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Demo_project_roundtrips_without_files()
    {
        string dir = TempDir();
        try
        {
            var vm = NewVm();
            vm.LoadDemoCommand.Execute(null);
            vm.Tracks[2].GainDb = -12;
            string path = Path.Combine(dir, "demo.smx");
            vm.SaveProject(path);
            vm.NewProject();

            await vm.OpenProjectAsync(path);
            Assert.Equal(5, vm.Tracks.Count);
            Assert.Equal("Hi-hat", vm.Tracks[2].Name);
            Assert.Equal(-12, vm.Tracks[2].GainDb);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Bpm_is_auto_filled_after_analysis_and_can_be_fixed_by_hand()
    {
        string dir = TempDir();
        try
        {
            string path = WriteClickWav(dir, "beat", 100, 24);
            var vm = NewVm();
            Assert.Equal(120, vm.Timeline.Bpm);
            await vm.LoadFilesAsync([path], replace: true);
            Pump(() => !vm.IsAnalyzing && vm.Analysis.TempoBpm is not null);

            Assert.NotNull(vm.Analysis.TempoBpm);
            Assert.InRange(vm.Timeline.Bpm, 97, 103);       // tự điền sau khi phân tích xong

            vm.Timeline.Bpm = 55;                           // người dùng sửa tay...
            vm.DetectBpmCommand.Execute(null);              // ...rồi bấm "Dò"
            Assert.InRange(vm.Timeline.Bpm, 97, 103);

            double detected = vm.Timeline.Bpm;
            vm.HalveBpmCommand.Execute(null);
            Assert.Equal(detected / 2, vm.Timeline.Bpm, 1);
            vm.DoubleBpmCommand.Execute(null);
            Assert.Equal(detected, vm.Timeline.Bpm, 1);

            vm.Timeline.Bpm = 200;
            vm.DoubleBpmCommand.Execute(null);              // vượt 300 thì bỏ qua
            Assert.Equal(200, vm.Timeline.Bpm);
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Autosave_is_written_when_dirty_cleared_on_save_and_recovered_after_a_crash()
    {
        string dir = TempDir();
        try
        {
            var store = new AutoSaveStore(Path.Combine(dir, "recovery"));
            string wav = WriteClickWav(dir, "take", 110, 4);

            // Phiên 1: làm việc, tự lưu, rồi "crash" (không Dispose).
            var vm = NewVm();
            vm.AutoSave = store;
            await vm.LoadFilesAsync([wav], replace: true);
            vm.Tracks[0].GainDb = -7;
            vm.SongName = "Unsaved idea";
            var r = vm.Tracks[0].BeginRegion(0.5, 1.5);
            vm.Tracks[0].SetEffect(r, RegionEffect.Reverse);
            vm.PollHistory(force: true);
            Assert.True(vm.IsDirty);
            vm.AutoSaveNow();
            Assert.NotNull(store.TryRead());

            // Lưu thủ công thì bản tự lưu không còn cần.
            string saved = Path.Combine(dir, "idea.smx");
            vm.SaveProject(saved);
            Assert.Null(store.TryRead());

            // Sửa tiếp rồi tự lưu lại; sau đó "crash".
            vm.Tracks[0].Pan = 0.5;
            vm.PollHistory(force: true);
            vm.AutoSaveNow();
            var info = store.TryRead();
            Assert.NotNull(info);
            Assert.Equal(saved, info!.OriginalPath);

            // Phiên 2: app mở lại, thấy bản tự lưu và khôi phục.
            var vm2 = NewVm();
            vm2.AutoSave = store;
            var found = vm2.FindRecovery();
            Assert.NotNull(found);
            Assert.True(await vm2.RecoverAsync(found!));

            Assert.Single(vm2.Tracks);
            Assert.Equal("take", vm2.Tracks[0].Name);
            Assert.Equal(-7, vm2.Tracks[0].GainDb);
            Assert.Equal(0.5, vm2.Tracks[0].Pan, 3);
            Assert.Equal("Unsaved idea", vm2.SongName);
            Assert.Equal(RegionEffect.Reverse, Assert.Single(vm2.Tracks[0].Regions).Effect);
            Assert.Equal(saved, vm2.ProjectPath);       // gắn lại với project gốc
            Assert.True(vm2.IsDirty);                   // vẫn chưa lưu so với file trên đĩa
            Assert.NotNull(store.TryRead());            // giữ bản tự lưu cho tới khi lưu thật

            // Thoát bình thường (Dispose) xóa bản phục hồi.
            vm2.Dispose();
            Assert.Null(store.TryRead());
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Autosave_waits_for_quiet_and_does_nothing_when_clean_or_disabled()
    {
        string dir = TempDir();
        try
        {
            var store = new AutoSaveStore(Path.Combine(dir, "recovery"));
            var vm = NewVm();
            vm.AutoSave = store;
            vm.PollHistory(force: true);                 // project trống, sạch
            Assert.Null(store.TryRead());

            vm.LoadDemoCommand.Execute(null);
            vm.PollHistory(force: true);                 // vừa đổi: chưa đủ yên để tự lưu
            Assert.Null(store.TryRead());

            vm.DiscardRecovery();
            var disabled = NewVm();                      // AutoSave = null: không làm gì
            disabled.LoadDemoCommand.Execute(null);
            disabled.AutoSaveNow();
            Assert.Null(store.TryRead());
            vm.Dispose();
            disabled.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Loop_region_survives_save_with_its_play_count()
    {
        var vm = NewVm();
        vm.LoadDemoCommand.Execute(null);
        var region = vm.Tracks[0].BeginRegion(2, 4);
        vm.Tracks[0].SetLoop(region, 3);
        Assert.Equal("Loop ×3", region.BadgeText);
        Assert.Equal(new LoopRegion(2 * 44100, 4 * 44100, 3), vm.ActiveLoop);

        vm.Tracks[0].SetLoop(region, 0);
        Assert.Equal("Loop ∞", region.BadgeText);
        Assert.Equal(0, vm.ActiveLoop!.Plays);

        var doc = ProjectFile.FromJson(ProjectFile.ToJson(vm.CaptureDocumentForTest()));
        Assert.Equal(0, doc.Tracks[0].Regions[0].LoopPlays);
        Assert.Equal("Loop", doc.Tracks[0].Regions[0].Effect);
        vm.Dispose();
    }

    private static List<SoundMaster.Core.Transcription.TranscribedNote> SampleNotes() =>
    [
        new(0.0, 0.5, 60, 100),
        new(0.5, 1.0, 64, 90),
        new(1.0, 2.0, 67, 80),
    ];

    [AvaloniaFact]
    public async Task Import_midi_creates_midi_track_opens_roll_plays_and_roundtrips_in_project()
    {
        string dir = TempDir();
        try
        {
            string midi = Path.Combine(dir, "melody.mid");
            SoundMaster.Core.Transcription.MidiExporter.Export(midi, SampleNotes(), bpm: 90, trackName: "Melody");

            var vm = NewVm();
            await vm.ImportMidiAsync(midi);

            var track = Assert.Single(vm.Tracks);
            Assert.True(track.IsMidi);
            Assert.Equal("Melody", track.Name);
            Assert.Equal([60, 64, 67], track.Notes.Select(n => n.MidiNote).ToArray());
            Assert.Equal(1.0, track.Notes[2].StartSeconds, 2);
            Assert.Equal(90, vm.Timeline.Bpm, 1);
            Assert.True(vm.IsPianoRoll);
            Assert.True(track.IsSelected);
            Assert.True(vm.IsPlaying);
            Assert.True(vm.Timeline.DurationSeconds >= 2.4);

            // Sửa nốt rồi lưu / mở lại project: nốt và cờ MIDI được giữ.
            vm.SelectedNote = track.Notes[0];
            vm.ReplaceNote(track.Notes[0], track.Notes[0] with { MidiNote = 62 });
            string proj = Path.Combine(dir, "song.smx");
            vm.SaveProject(proj);

            var vm2 = NewVm();
            Assert.True(await vm2.OpenProjectAsync(proj));
            var t2 = Assert.Single(vm2.Tracks);
            Assert.True(t2.IsMidi);
            Assert.Equal([62, 64, 67], t2.Notes.Select(n => n.MidiNote).ToArray());
            vm2.Dispose();
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [AvaloniaFact]
    public void Note_editing_split_merge_quantize_add_and_replace()
    {
        var vm = NewVm();
        vm.LoadDemoCommand.Execute(null);
        var track = vm.Tracks.First(t => t.Name == "Lead");
        vm.Timeline.Bpm = 120;      // 1 phách = 0,5 s; lưới 1/16 = 0,125 s
        vm.GridIndex = 2;
        track.Notes = [new(0.06, 0.52, 60, 90, 0.3), new(0.55, 1.07, 64, 90)];
        track.RequestSelect();

        // Sửa tay: đổi cao độ → thành nốt chính.
        var first = track.Notes[0];
        Assert.True(first.IsDoubtful);
        vm.ReplaceNote(first, first with { MidiNote = 61 });
        Assert.Equal(61, track.Notes[0].MidiNote);
        Assert.False(track.Notes[0].IsDoubtful);

        // Tách giữa nốt thứ hai (playhead ngoài nốt → tách đôi).
        vm.SelectedNote = track.Notes[1];
        vm.Timeline.PositionSeconds = 0;
        vm.SplitNoteCommand.Execute(null);
        Assert.Equal(3, track.Notes.Count);
        Assert.Equal(track.Notes[1].EndSeconds, track.Notes[2].StartSeconds, 6);

        // Gộp lại: nốt chọn (nửa đầu) + nốt kế → một nốt.
        vm.SelectedNote = track.Notes[1];
        vm.MergeNoteCommand.Execute(null);
        Assert.Equal(2, track.Notes.Count);

        // Căn nhịp: đầu / cuối vào bội của 0,125 s.
        vm.QuantizeNotesCommand.Execute(null);
        Assert.All(track.Notes, n =>
        {
            Assert.Equal(0, n.StartSeconds / 0.125 - Math.Round(n.StartSeconds / 0.125), 6);
            Assert.Equal(0, n.EndSeconds / 0.125 - Math.Round(n.EndSeconds / 0.125), 6);
            Assert.True(n.Duration >= 0.125 - 1e-9);
        });

        // Vẽ nốt mới bám lưới.
        vm.AddNote(2.07, 70);
        var added = track.Notes.Single(n => n.MidiNote == 70);
        Assert.Equal(2.0, added.StartSeconds, 6);
        Assert.Equal(2.125, added.EndSeconds, 6);
        Assert.Same(added, vm.SelectedNote);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task Transcribe_demo_bass_and_lead_then_export_midi()
    {
        string dir = TempDir();
        try
        {
            var vm = NewVm();
            vm.LoadDemoCommand.Execute(null);
            var bass = vm.Tracks.First(t => t.Name == "Bass");
            var lead = vm.Tracks.First(t => t.Name == "Lead");

            Assert.False(bass.HasNotes);
            await vm.TranscribeTrackAsync(bass);
            await vm.TranscribeTrackAsync(lead);
            Assert.False(bass.IsTranscribing);

            Assert.True(bass.HasNotes, vm.Status);
            Assert.True(lead.HasNotes, vm.Status);
            // Stem demo là các nốt có cao độ rõ: các nốt bóc ra phải nằm trong dải hợp lý và có độ dài sensible.
            Assert.All(bass.Notes.Concat(lead.Notes), n =>
            {
                Assert.InRange(n.MidiNote, 28, 96);
                Assert.True(n.Duration >= 0.05);
                Assert.True(n.EndSeconds <= 16.5);
            });
            Assert.Contains("Bóc được", vm.Status);

            string path = Path.Combine(dir, "bass.mid");
            vm.ExportMidi(bass, path);
            Assert.True(File.Exists(path));
            var midi = new NAudio.Midi.MidiFile(path, strictChecking: false);
            var ons = Enumerable.Range(0, midi.Tracks).SelectMany(t => midi.Events[t])
                .OfType<NAudio.Midi.NoteOnEvent>().Where(e => e.Velocity > 0).ToList();
            Assert.Equal(bass.Notes.Count, ons.Count);
            Assert.Contains("Đã xuất", vm.Status);

            // Kick (bộ gõ) không được biến thành đống nốt rác.
            var kick = vm.Tracks.First(t => t.Name == "Kick");
            await vm.TranscribeTrackAsync(kick);
            Assert.True(kick.Notes.Count < 40, $"kick cho {kick.Notes.Count} nốt");
            vm.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(100.1, 100)]
    [InlineData(99.8, 100)]
    [InlineData(112.4, 112.4)]
    [InlineData(87.6, 87.6)]
    public void RoundBpm_snaps_near_integers(double raw, double expected)
        => Assert.Equal(expected, MainViewModel.RoundBpm(raw), 6);
}
