using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Analysis;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Editing;
using SoundMaster.Core.Project;

namespace SoundMaster.App.ViewModels;

/// <summary>Ảnh chụp trạng thái có thể hoàn tác: mô tả project + các stem (tham chiếu, không sao chép âm thanh).</summary>
public sealed record EditorState(ProjectDocument Doc, IReadOnlyList<AudioTrack> Audio);

/// <summary>Lưu/mở project (.smx), undo/redo và tự điền BPM.</summary>
public partial class MainViewModel
{
    private static readonly TimeSpan HistoryQuietPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HistoryPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private UndoHistory<EditorState>? _history;
    private string _savedKey = "";
    private TimeSpan _lastHistoryPoll;
    private bool _restoring;
    private bool _autoBpmPending;

    /// <summary>File project đang mở (null nếu chưa lưu lần nào).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial string? ProjectPath { get; set; }

    /// <summary>Có thay đổi chưa lưu so với lần lưu/mở gần nhất.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial bool CanUndo { get; set; }

    [ObservableProperty]
    public partial bool CanRedo { get; set; }

    public string WindowTitle
        => $"{(ProjectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(ProjectPath))}{(IsDirty ? " •" : "")} — SoundMaster";

    // ---------- chụp / khôi phục trạng thái ----------

    /// <summary>Tài liệu project của trạng thái hiện tại (dùng trong test).</summary>
    public ProjectDocument CaptureDocumentForTest() => Capture().Doc;

    private EditorState Capture()
    {
        var doc = new ProjectDocument
        {
            SongName = SongName,
            Bpm = Timeline.Bpm,
            MasterGainDb = (float)MasterGainDb,
            LimiterEnabled = LimiterEnabled,
            LoopEnabled = LoopEnabled,
            Speed = SpeedFactor,
        };

        foreach (var t in Tracks)
        {
            doc.Tracks.Add(new ProjectTrack
            {
                Name = t.Name,
                SourceFull = t.Audio.SourcePath,
                Demo = t.Audio.DemoKey,
                ColorHex = t.ColorHex,
                GainDb = t.Strip.GainDb,
                Pan = t.Strip.Pan,
                Muted = t.IsMuted,
                Soloed = t.IsSolo,
                Armed = t.IsArmed,
                Locked = t.IsLocked,
                Regions = t.Regions
                    .Select(r => new ProjectRegion { Start = r.StartSeconds, End = r.EndSeconds, Effect = r.Effect.ToString(), LoopPlays = r.LoopPlays })
                    .ToList(),
            });
        }

        return new EditorState(doc, Tracks.Select(t => t.Audio).ToList());
    }

    private static string KeyOf(EditorState state) => ProjectFile.Key(state.Doc);

    private static void ApplyTrackState(TrackViewModel track, ProjectTrack saved)
    {
        track.Name = saved.Name;
        track.ColorHex = saved.ColorHex;
        track.GainDb = saved.GainDb;
        track.Pan = saved.Pan;
        track.IsMuted = saved.Muted;
        track.IsSolo = saved.Soloed;
        track.IsArmed = saved.Armed;

        track.Regions.Clear();
        track.ActiveRegion = null;
        foreach (var r in saved.Regions)
        {
            var region = track.BeginRegion(r.Start, r.End);
            region.Effect = Enum.TryParse<RegionEffect>(r.Effect, out var effect) ? effect : RegionEffect.None;
            region.LoopPlays = Math.Max(0, r.LoopPlays);
        }
        track.ActiveRegion = null;
        track.NotifyRegionsChanged();

        track.IsLocked = saved.Locked; // sau cùng để không chặn việc dựng lại vùng
    }

    private void ApplyGlobalState(ProjectDocument doc)
    {
        SongName = doc.SongName;
        Timeline.Bpm = Math.Clamp(doc.Bpm, 30, 300);
        MasterGainDb = doc.MasterGainDb;
        LimiterEnabled = doc.LimiterEnabled;
        LoopEnabled = doc.LoopEnabled;
        SpeedFactor = Math.Clamp(doc.Speed, TimeStretchProvider.MinSpeed, TimeStretchProvider.MaxSpeed);
    }

    /// <summary>Đưa app về đúng trạng thái đã chụp (dùng cho undo/redo).</summary>
    private void Restore(EditorState state)
    {
        _restoring = true;
        try
        {
            bool sameStems = Tracks.Count == state.Audio.Count;
            for (int i = 0; sameStems && i < Tracks.Count; i++)
                sameStems = ReferenceEquals(Tracks[i].Audio, state.Audio[i]);

            if (!sameStems)
            {
                // Số lượng/thứ tự stem đổi: dựng lại danh sách track (giữ nguyên vị trí phát).
                Tracks.Clear();
                foreach (var audio in state.Audio) AddTrackViewModel(audio);
                _autoBpmPending = false;
                AfterTracksChanged(resetTransport: false);
            }

            for (int i = 0; i < Tracks.Count; i++) ApplyTrackState(Tracks[i], state.Doc.Tracks[i]);
            ApplyGlobalState(state.Doc);
            UpdateLoopRegion(null);
        }
        finally
        {
            _restoring = false;
        }
    }

    // ---------- undo / redo ----------

    private void InitHistory()
    {
        var state = Capture();
        var key = KeyOf(state);
        _history = new UndoHistory<EditorState>(state, key, HistoryQuietPeriod);
        _savedKey = key;
    }

    /// <summary>Gọi mỗi nhịp timer: ghi nhận thay đổi (gộp thành bước khi yên) và cập nhật cờ chưa lưu.</summary>
    public void PollHistory(bool force = false)
    {
        if (_history is null || _restoring || (IsBusy && !force)) return;
        var now = _clock.Elapsed;
        if (!force && now - _lastHistoryPoll < HistoryPollInterval) return;
        _lastHistoryPoll = now;

        var state = Capture();
        string key = KeyOf(state);
        _history.Observe(state, key, now);
        CanUndo = _history.CanUndo(key);
        CanRedo = _history.CanRedo(key);
        IsDirty = key != _savedKey;
        AutoSaveIfNeeded(key, now);
    }

    /// <summary>Chốt ngay các chỉnh sửa đang chờ gộp thành một bước undo (trước khi lưu, hoặc khi cần ranh giới bước).</summary>
    public void CommitPendingEdits()
    {
        if (_history is null || _restoring) return;
        var state = Capture();
        _history.Flush(state, KeyOf(state));
        PollHistory(force: true);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_history is null || IsBusy) return;
        var current = Capture();
        if (!_history.TryUndo(current, KeyOf(current), out var target)) return;
        RestoreFromHistory(target);
    }

    [RelayCommand]
    private void Redo()
    {
        if (_history is null || IsBusy) return;
        var current = Capture();
        if (!_history.TryRedo(current, KeyOf(current), out var target)) return;
        RestoreFromHistory(target);
    }

    private void RestoreFromHistory(EditorState target)
    {
        Restore(target);
        var actual = Capture();
        _history!.Rebase(actual, KeyOf(actual)); // dù làm tròn khác chút cũng không sinh bước mới
        PollHistory(force: true);
    }

    // ---------- lưu / mở project ----------

    /// <summary>Lưu project ra <paramref name="path"/> (đường dẫn file gốc được lưu tương đối khi có thể).</summary>
    public void SaveProject(string path)
    {
        var doc = Capture().Doc;
        foreach (var t in doc.Tracks)
            if (t.SourceFull is not null) t.Source = ProjectFile.MakeSource(path, t.SourceFull);

        ProjectFile.Save(path, doc);
        ProjectPath = Path.GetFullPath(path);
        _savedKey = KeyOf(Capture());
        PollHistory(force: true);
        Status = $"Đã lưu project: {Path.GetFileName(path)}";
        AutoSave?.Clear(); // đã lưu thật: bản tự lưu không còn cần
        _autoSavedKey = "";
    }

    /// <summary>Mở project: nạp lại stem từ file gốc rồi áp dụng cài đặt đã lưu.</summary>
    public async Task<bool> OpenProjectAsync(string path, bool clearAutoSave = true)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            var doc = await Task.Run(() => ProjectFile.Load(path));
            var audio = new List<AudioTrack>();
            var kept = new List<ProjectTrack>();
            var problems = new List<string>();
            IReadOnlyList<AudioTrack>? demo = null;

            for (int i = 0; i < doc.Tracks.Count; i++)
            {
                var saved = doc.Tracks[i];
                Status = $"Đang nạp {i + 1}/{doc.Tracks.Count}: {saved.Name}";
                try
                {
                    if (saved.Demo is not null)
                    {
                        demo ??= DemoStems.Generate();
                        var stem = demo.FirstOrDefault(s => s.DemoKey == saved.Demo);
                        if (stem is null)
                        {
                            problems.Add($"{saved.Name} (stem demo không còn)");
                            continue;
                        }
                        audio.Add(stem);
                    }
                    else
                    {
                        string? file = ProjectFile.ResolveSource(saved, path);
                        if (file is null)
                        {
                            problems.Add($"{saved.Name} (không thấy file)");
                            continue;
                        }
                        audio.Add(await Task.Run(() => AudioTrack.Load(file)));
                    }
                    kept.Add(saved);
                }
                catch (Exception ex)
                {
                    problems.Add($"{saved.Name} ({ex.Message})");
                }
            }

            _restoring = true;
            try
            {
                _engine.Pause();
                IsPlaying = false;
                Tracks.Clear();
                foreach (var stem in audio) AddTrackViewModel(stem);
                _autoBpmPending = false;
                AfterTracksChanged(resetTransport: true);

                for (int i = 0; i < Tracks.Count; i++) ApplyTrackState(Tracks[i], kept[i]);
                ApplyGlobalState(doc);
                UpdateLoopRegion(null);
            }
            finally
            {
                _restoring = false;
            }

            ProjectPath = Path.GetFullPath(path);
            var state = Capture();
            string key = KeyOf(state);
            _history = new UndoHistory<EditorState>(state, key, HistoryQuietPeriod);
            // Thiếu track thì project hiện tại khác file đã lưu: giữ trạng thái "chưa lưu" để người dùng biết.
            _savedKey = problems.Count == 0 ? key : "";
            PollHistory(force: true);

            Status = problems.Count == 0
                ? $"Đã mở project: {Path.GetFileName(path)}"
                : $"Đã mở project, thiếu {problems.Count} track: {string.Join("; ", problems)}";
            if (clearAutoSave) AutoSave?.Clear();
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Không mở được project: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Bắt đầu project trống.</summary>
    public void NewProject()
    {
        if (IsBusy) return;
        _restoring = true;
        try
        {
            Tracks.Clear();
            _engine.Pause();
            IsPlaying = false;
            _autoBpmPending = false;
            AfterTracksChanged(resetTransport: true);
            ApplyGlobalState(new ProjectDocument { SongName = "Untitled" });
        }
        finally
        {
            _restoring = false;
        }

        ProjectPath = null;
        var state = Capture();
        string key = KeyOf(state);
        _history = new UndoHistory<EditorState>(state, key, HistoryQuietPeriod);
        _savedKey = key;
        PollHistory(force: true);
        Status = "Project mới.";
        AutoSave?.Clear();
        _autoSavedKey = "";
    }

    // ---------- tự lưu & phục hồi ----------

    private static readonly TimeSpan AutoSaveQuiet = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AutoSaveMinInterval = TimeSpan.FromSeconds(10);

    private string _autoSavedKey = "";
    private string _lastKeySeen = "";
    private TimeSpan _lastChangeAt;
    private TimeSpan _lastAutoSaveAt = TimeSpan.FromSeconds(-1000);

    /// <summary>Kho bản tự lưu; null = tắt tự lưu (mặc định, để test không ghi vào thư mục người dùng).</summary>
    public AutoSaveStore? AutoSave { get; set; }

    /// <summary>Gọi mỗi lần poll: tự lưu khi có thay đổi chưa lưu, đã yên vài giây và cách lần trước đủ lâu.</summary>
    private void AutoSaveIfNeeded(string key, TimeSpan now)
    {
        if (key != _lastKeySeen)
        {
            _lastKeySeen = key;
            _lastChangeAt = now;
        }

        if (AutoSave is null || !IsDirty || key == _autoSavedKey) return;
        if (now - _lastChangeAt < AutoSaveQuiet || now - _lastAutoSaveAt < AutoSaveMinInterval) return;
        AutoSaveNow();
    }

    /// <summary>Ghi ngay một bản tự lưu của trạng thái hiện tại.</summary>
    public void AutoSaveNow()
    {
        if (AutoSave is null) return;
        try
        {
            var state = Capture();
            AutoSave.Write(state.Doc, ProjectPath);
            _autoSavedKey = KeyOf(state);
            _lastAutoSaveAt = _clock.Elapsed;
        }
        catch
        {
            // Tự lưu hỏng (ổ đầy, bị khóa...) không được làm phiền người dùng; lần poll sau thử lại.
        }
    }

    /// <summary>Bản tự lưu còn sót từ phiên trước (app bị đóng đột ngột), nếu có.</summary>
    public AutoSaveInfo? FindRecovery() => AutoSave?.TryRead();

    /// <summary>Khôi phục bản tự lưu: nạp lại và đánh dấu "chưa lưu" (gắn lại với project gốc nếu file còn).</summary>
    public async Task<bool> RecoverAsync(AutoSaveInfo info)
    {
        if (!await OpenProjectAsync(info.ProjectFile, clearAutoSave: false)) return false;

        ProjectPath = info.OriginalPath is { } original && File.Exists(original) ? original : null;
        _savedKey = "";                       // khác bản đã lưu trên đĩa → vẫn là "chưa lưu"
        _autoSavedKey = KeyOf(Capture());     // bản tự lưu hiện có đã khớp trạng thái này
        PollHistory(force: true);
        Status = $"Đã khôi phục bản tự lưu lúc {info.SavedAtUtc.ToLocalTime():HH:mm dd/MM}. Nhớ lưu lại project.";
        return true;
    }

    /// <summary>Người dùng bỏ bản phục hồi.</summary>
    public void DiscardRecovery() => AutoSave?.Clear();

    // ---------- tự điền BPM ----------

    /// <summary>Làm tròn BPM dò được: số nguyên nếu sát (±0.25), ngược lại một chữ số lẻ.</summary>
    public static double RoundBpm(double bpm)
    {
        double whole = Math.Round(bpm);
        return Math.Abs(bpm - whole) <= 0.25 ? whole : Math.Round(bpm, 1);
    }

    private void TryAutoFillBpm(MixAnalysis result)
    {
        if (!_autoBpmPending) return;
        _autoBpmPending = false;
        if (result.TempoBpm is not { } bpm) return;

        Timeline.Bpm = Math.Clamp(RoundBpm(bpm), 30, 300);
        Status = $"BPM tự dò: {Timeline.BpmText} (bấm ½ / ×2 nếu bị nhầm nhịp).";
    }

    /// <summary>Điền BPM dò được từ audio vào ô BPM.</summary>
    [RelayCommand]
    private void DetectBpm()
    {
        if (Analysis.TempoBpm is not { } bpm)
        {
            Status = IsAnalyzing
                ? "Đang phân tích, thử lại sau giây lát."
                : "Không dò được BPM (nhạc không có nhịp đều đủ rõ).";
            return;
        }

        Timeline.Bpm = Math.Clamp(RoundBpm(bpm), 30, 300);
        Status = $"BPM tự dò: {Timeline.BpmText}";
    }

    [RelayCommand]
    private void HalveBpm()
    {
        double bpm = Timeline.Bpm / 2;
        if (bpm >= 30) Timeline.Bpm = Math.Round(bpm, 2);
    }

    [RelayCommand]
    private void DoubleBpm()
    {
        double bpm = Timeline.Bpm * 2;
        if (bpm <= 300) Timeline.Bpm = Math.Round(bpm, 2);
    }
}
