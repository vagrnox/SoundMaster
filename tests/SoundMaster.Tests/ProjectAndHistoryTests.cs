using SoundMaster.Core.Editing;
using SoundMaster.Core.Project;
using Xunit;

namespace SoundMaster.Tests;

public class ProjectFileTests
{
    private static ProjectDocument Sample() => new()
    {
        SongName = "Neon Skyline",
        Bpm = 112,
        MasterGainDb = -2.5f,
        Speed = 0.8,
        Tracks =
        [
            new ProjectTrack
            {
                Name = "Vocals", Source = "stems\\vocals.wav", SourceFull = "C:\\music\\stems\\vocals.wav",
                ColorHex = "#E91E63", GainDb = -3, Pan = -0.25f, Muted = true, Locked = true,
                Regions = [new ProjectRegion { Start = 1.5, End = 4, Effect = "FadeIn" }],
            },
            new ProjectTrack { Name = "Kick", Demo = "Kick", ColorHex = "#2196F3" },
        ],
    };

    [Fact]
    public void Json_roundtrip_keeps_everything()
    {
        var doc = Sample();
        var back = ProjectFile.FromJson(ProjectFile.ToJson(doc));
        Assert.Equal(ProjectFile.Key(doc), ProjectFile.Key(back));
        Assert.Equal("Neon Skyline", back.SongName);
        Assert.Equal(-0.25f, back.Tracks[0].Pan);
        Assert.Equal("FadeIn", back.Tracks[0].Regions[0].Effect);
        Assert.Equal("Kick", back.Tracks[1].Demo);
    }

    [Fact]
    public void Save_and_load_use_the_file_system_atomically()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm_proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "song" + ProjectDocument.Extension);
            ProjectFile.Save(path, Sample());
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("Neon Skyline", ProjectFile.Load(path).SongName);

            // Ghi đè lần hai vẫn ổn.
            var changed = Sample();
            changed.Bpm = 90;
            ProjectFile.Save(path, changed);
            Assert.Equal(90, ProjectFile.Load(path).Bpm);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Newer_versions_and_garbage_are_rejected_with_clear_errors()
    {
        Assert.Throws<InvalidDataException>(() => ProjectFile.FromJson("{\"Version\": 99}"));
        string path = Path.Combine(Path.GetTempPath(), "sm_bad_" + Guid.NewGuid().ToString("N") + ".smx");
        File.WriteAllText(path, "not json at all");
        try
        {
            Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Source_paths_are_relative_when_possible_and_resolve_after_moving()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm_src_" + Guid.NewGuid().ToString("N"));
        string stems = Path.Combine(root, "stems");
        Directory.CreateDirectory(stems);
        try
        {
            string audio = Path.Combine(stems, "a.wav");
            File.WriteAllText(audio, "x");
            string project = Path.Combine(root, "p.smx");

            string stored = ProjectFile.MakeSource(project, audio);
            Assert.Equal(Path.Combine("stems", "a.wav"), stored);

            var track = new ProjectTrack { Source = stored, SourceFull = audio };
            Assert.Equal(audio, ProjectFile.ResolveSource(track, project));

            // Dời cả thư mục: đường dẫn tương đối vẫn tìm ra file; đường dẫn đầy đủ cũ không còn.
            string moved = root + "_moved";
            Directory.Move(root, moved);
            try
            {
                string newProject = Path.Combine(moved, "p.smx");
                Assert.Equal(Path.Combine(moved, "stems", "a.wav"), ProjectFile.ResolveSource(track, newProject));
            }
            finally
            {
                Directory.Move(moved, root);
            }

            File.Delete(audio);
            Assert.Null(ProjectFile.ResolveSource(track, project));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class UndoHistoryTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    [Fact]
    public void Edits_are_coalesced_until_the_state_settles()
    {
        var h = new UndoHistory<int>(0, "0", Quiet);
        var t = TimeSpan.Zero;

        // Kéo volume liên tục: 1,2,3,4 trong 300 ms rồi yên.
        for (int v = 1; v <= 4; v++)
        {
            h.Observe(v, v.ToString(), t);
            t += TimeSpan.FromMilliseconds(75);
        }
        Assert.True(h.CanUndo("4"));
        h.Observe(4, "4", t + Quiet);

        Assert.True(h.TryUndo(4, "4", out int back));
        Assert.Equal(0, back);          // một bước duy nhất về trạng thái trước khi kéo
        Assert.False(h.TryUndo(0, "0", out _));
    }

    [Fact]
    public void Undo_redo_walk_the_history_and_new_edits_clear_redo()
    {
        var h = new UndoHistory<string>("a", "a", Quiet);
        var t = TimeSpan.Zero;
        void Edit(string s)
        {
            h.Observe(s, s, t);
            t += Quiet;
            h.Observe(s, s, t);
            t += Quiet;
        }

        Edit("b");
        Edit("c");
        Assert.True(h.TryUndo("c", "c", out var s1));
        Assert.Equal("b", s1);
        Assert.True(h.TryUndo("b", "b", out var s2));
        Assert.Equal("a", s2);
        Assert.False(h.TryUndo("a", "a", out _));

        Assert.True(h.TryRedo("a", "a", out var s3));
        Assert.Equal("b", s3);
        Assert.True(h.CanRedo("b"));

        Edit("x");                       // sửa mới: không redo được nữa
        Assert.False(h.CanRedo("x"));
        Assert.False(h.TryRedo("x", "x", out _));
    }

    [Fact]
    public void Undo_flushes_an_unsettled_edit_first()
    {
        var h = new UndoHistory<int>(0, "0", Quiet);
        h.Observe(7, "7", TimeSpan.Zero);      // vừa đổi, chưa yên
        Assert.True(h.CanUndo("7"));
        Assert.True(h.TryUndo(7, "7", out int back));
        Assert.Equal(0, back);
        Assert.True(h.TryRedo(0, "0", out int fwd));
        Assert.Equal(7, fwd);
    }

    [Fact]
    public void Changing_back_to_baseline_cancels_the_pending_edit_and_rebase_makes_no_step()
    {
        var h = new UndoHistory<int>(0, "0", Quiet);
        h.Observe(1, "1", TimeSpan.Zero);
        h.Observe(0, "0", TimeSpan.FromMilliseconds(100)); // trả về như cũ
        Assert.False(h.CanUndo("0"));

        h.Rebase(5, "5");
        Assert.False(h.CanUndo("5"));
        h.Reset(9, "9");
        Assert.Equal("9", h.BaselineKey);
    }
}

public class AutoSaveStoreTests
{
    [Fact]
    public void Write_read_clear_cycle()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm_auto_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AutoSaveStore(dir);
            Assert.Null(store.TryRead());

            var doc = new ProjectDocument
            {
                SongName = "Crash test",
                Tracks = [new ProjectTrack { Name = "A", Source = "rel/a.wav", SourceFull = "C:/x/a.wav" }],
            };
            store.Write(doc, "C:/music/song.smx");

            var info = store.TryRead();
            Assert.NotNull(info);
            Assert.Equal("C:/music/song.smx", info!.OriginalPath);
            Assert.True((DateTime.UtcNow - info.SavedAtUtc).TotalSeconds < 30);

            var loaded = ProjectFile.Load(info.ProjectFile);
            Assert.Equal("Crash test", loaded.SongName);
            Assert.Null(loaded.Tracks[0].Source);                      // tự lưu chỉ dùng đường dẫn đầy đủ
            Assert.Equal("C:/x/a.wav", loaded.Tracks[0].SourceFull);

            // Ghi đè lần hai, rồi xóa sạch.
            store.Write(new ProjectDocument { SongName = "Second" }, null);
            Assert.Null(store.TryRead()!.OriginalPath);
            store.Clear();
            Assert.Null(store.TryRead());
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_autosave_is_ignored_instead_of_crashing_startup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm_auto_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AutoSaveStore(dir);
            File.WriteAllText(store.ProjectFilePath, "{ broken");
            File.WriteAllText(Path.Combine(dir, "autosave.json"), "{}");
            Assert.Null(store.TryRead());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
