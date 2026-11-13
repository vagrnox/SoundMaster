using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Analysis;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".aiff", ".aif", ".m4a", ".wma"];

    private readonly MixerEngine _engine = new();
    private readonly TimeStretchProvider _stretch;
    private readonly DispatcherTimer _timer;
    private AudioOutput? _output;
    private readonly bool _enableAudioOutput;
    private DateTime _lastTick = DateTime.UtcNow;

    public MainViewModel() : this(enableAudioOutput: true)
    {
    }

    /// <param name="enableAudioOutput">false khi chạy test giao diện: không mở thiết bị âm thanh.</param>
    public MainViewModel(bool enableAudioOutput)
    {
        _enableAudioOutput = enableAudioOutput;
        _stretch = new TimeStretchProvider(_engine);
        Timeline.SeekRequested += OnSeekRequested;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public TimelineViewModel Timeline { get; } = new();
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    [ObservableProperty]
    public partial string SongName { get; set; } = "Untitled";

    /// <summary>Kết quả phân tích (LUFS, giọng) của bản mix; tính nền sau mỗi lần nạp track.</summary>
    [ObservableProperty]
    public partial MixAnalysis Analysis { get; set; } = MixAnalysis.Empty;

    [ObservableProperty]
    public partial bool IsAnalyzing { get; set; }

    /// <summary>Tốc độ phát (0.1x–3x, giữ nguyên cao độ); 1 = bình thường.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double SpeedFactor { get; set; } = 1;

    public string SpeedText => FormatSpeed(SpeedFactor);

    public static string FormatSpeed(double speed)
        => Math.Round(speed, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x";

    partial void OnSpeedFactorChanged(double value) => _stretch.Speed = value;

    /// <summary>Thông tin file sẽ xuất bản (placeholder, sau này cho nhập).</summary>
    [ObservableProperty]
    public partial string ExportSource { get; set; } = "—";

    [ObservableProperty]
    public partial string ExportCreated { get; set; } = "—";

    [ObservableProperty]
    public partial string ExportQuality { get; set; } = "—";

    /// <summary>Đường bao biên độ tổng của toàn bài (cho thanh waveform tổng quan ở chân cửa sổ).</summary>
    [ObservableProperty]
    public partial float[] OverviewPeaks { get; set; } = [];

    private const int OverviewBins = 2048;

    private static float[] BuildOverview(IReadOnlyList<AudioTrack> tracks)
    {
        int frames = tracks.Count == 0 ? 0 : tracks.Max(t => t.FrameCount);
        if (frames == 0) return [];
        var bins = new float[OverviewBins];
        double framesPerBin = (double)frames / OverviewBins;
        foreach (var t in tracks)
        {
            var peaks = t.Peaks;
            for (int b = 0; b < OverviewBins; b++)
            {
                int from = (int)(b * framesPerBin / AudioTrack.PeakBucketFrames);
                int to = Math.Min(peaks.Length, Math.Max(from + 1, (int)((b + 1) * framesPerBin / AudioTrack.PeakBucketFrames)));
                float max = 0;
                for (int i = from; i < to; i++) if (peaks[i] > max) max = peaks[i];
                bins[b] += max;
            }
        }
        float top = bins.Max();
        if (top > 0) for (int b = 0; b < bins.Length; b++) bins[b] /= top;
        return bins;
    }

    private CancellationTokenSource? _analysisCts;

    private async void StartAnalysis()
    {
        _analysisCts?.Cancel();
        _analysisCts?.Dispose();
        var cts = _analysisCts = new CancellationTokenSource();
        var snapshot = Tracks.Select(t => t.Strip.Track).ToList();
        if (snapshot.Count == 0)
        {
            Analysis = MixAnalysis.Empty;
            IsAnalyzing = false;
            return;
        }

        IsAnalyzing = true;
        try
        {
            var result = await Task.Run(() => MixAnalyzer.Analyze(snapshot, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) Analysis = result;
        }
        catch (OperationCanceledException)
        {
            return; // đã có lần phân tích mới hơn
        }
        catch (Exception ex)
        {
            Analysis = MixAnalysis.Empty;
            Status = $"Không phân tích được: {ex.Message}";
        }
        if (!cts.IsCancellationRequested) IsAnalyzing = false;
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Dùng nút ••• để mở thư mục stems hoặc nạp stem demo.";

    /// <summary>Lỗi mở thiết bị âm thanh (rỗng nếu không có); chỉ hiện khi có lỗi.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAudioError))]
    public partial string AudioError { get; set; } = "";

    public bool HasAudioError => AudioError.Length > 0;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseText))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText))]
    public partial double MasterGainDb { get; set; }

    /// <summary>Khóa volume tổng (và limiter): không cho chỉnh thêm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMasterEditable), nameof(MasterAccentColor))]
    public partial bool IsMasterLocked { get; set; }

    [ObservableProperty]
    public partial bool LimiterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool LoopEnabled { get; set; }

    [ObservableProperty]
    public partial double MasterMeterLeftDb { get; set; } = -90;

    [ObservableProperty]
    public partial double MasterMeterRightDb { get; set; } = -90;

    public bool IsMasterEditable => !IsMasterLocked;

    /// <summary>Màu thanh volume tổng: trắng xám, hoặc xám đậm khi bị khóa.</summary>
    public Avalonia.Media.Color MasterAccentColor => IsMasterLocked ? TrackViewModel.LockedColor : Avalonia.Media.Color.Parse("#D5DDE3");
    public string PlayPauseText => IsPlaying ? "⏸" : "▶";
    public string MasterGainText => MasterGainDb <= MixerEngine.MasterMinDb ? "Volume: -∞ dB" : $"Volume: {MasterGainDb:0.0} dB";

    /// <summary>Tên bài hát đã lọc ký tự không hợp lệ, dùng làm tên file khi xuất.</summary>
    public string SongFileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(SongName.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Untitled" : cleaned;
        }
    }

    partial void OnMasterGainDbChanged(double value) => _engine.MasterGainDb = (float)value;
    partial void OnLimiterEnabledChanged(bool value) => _engine.Limiter.Enabled = value;
    partial void OnLoopEnabledChanged(bool value) => _engine.Loop = value;

    // ---------- nạp stem ----------

    public static bool IsAudioFile(string path)
        => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public Task OpenFolderAsync(string folder)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(IsAudioFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
        {
            Status = "Thư mục không có file âm thanh (wav, mp3, ...).";
            return Task.CompletedTask;
        }

        return LoadFilesAsync(files, replace: true);
    }

    public async Task LoadFilesAsync(IReadOnlyList<string> paths, bool replace)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var loaded = new List<AudioTrack>();
            var errors = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                Status = $"Đang giải mã {i + 1}/{paths.Count}: {Path.GetFileName(path)}";
                try
                {
                    loaded.Add(await Task.Run(() => AudioTrack.Load(path)));
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)} ({ex.Message})");
                }
            }

            ApplyTracks(loaded, replace);
            Status = errors.Count == 0
                ? $"Đã nạp {loaded.Count} track."
                : $"Đã nạp {loaded.Count} track, lỗi {errors.Count}: {string.Join("; ", errors)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void LoadDemo()
    {
        if (IsBusy) return;
        ApplyTracks(DemoStems.Generate(), replace: true);
        Status = "Đã nạp 5 stem demo (kick, bass, hi-hat, pad, lead).";
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsBusy) return;
        ApplyTracks([], replace: true);
        Status = "Đã xóa các track.";
    }

    private void ApplyTracks(IReadOnlyList<AudioTrack> tracks, bool replace)
    {
        _engine.Pause();
        IsPlaying = false;
        if (replace) Tracks.Clear();
        foreach (var track in tracks)
        {
            int index = Tracks.Count;
            var created = new TrackViewModel(new ChannelStrip(track), Timeline, index + 1, TrackPalette.ForIndex(index));
            HookTrack(created);
            Tracks.Add(created);
        }

        _engine.SetStrips(Tracks.Select(t => t.Strip));
        Timeline.DurationSeconds = MixerEngine.FramesToSeconds(_engine.LengthFrames);
        if (replace)
        {
            Timeline.PositionSeconds = 0;
            _engine.Seek(0);
            _stretch.Reset();
            Timeline.ZoomToFit();
        }
        OverviewPeaks = BuildOverview(Tracks.Select(t => t.Strip.Track).ToList());
        UpdateLoopRegion(null);
        EnsureOutput();
        StartAnalysis();
    }

    // ---------- chọn track, vùng chọn, vùng lặp ----------

    private RegionViewModel? _loopOwner;

    /// <summary>Đoạn đang lặp (hiệu ứng Loop), null nếu không có.</summary>
    public LoopRegion? ActiveLoop => _engine.LoopRegion;

    /// <summary>Track đang chọn (null nếu chưa chọn track nào).</summary>
    public TrackViewModel? SelectedTrack => Tracks.FirstOrDefault(t => t.IsSelected);

    private void HookTrack(TrackViewModel track)
    {
        track.SelectRequested += OnTrackSelectRequested;
        track.RegionsChanged += t => UpdateLoopRegion(t);
    }

    private void OnTrackSelectRequested(TrackViewModel track)
    {
        foreach (var other in Tracks)
        {
            if (ReferenceEquals(other, track)) continue;
            other.IsSelected = false;
            other.DeactivateRegion();
        }
        track.IsSelected = true;
    }

    /// <summary>
    /// Đồng bộ vùng lặp của engine với các vùng có hiệu ứng Loop. Chỉ một vùng lặp tại một thời điểm:
    /// đặt Loop ở vùng mới thì vùng Loop cũ bị bỏ hiệu ứng.
    /// </summary>
    private void UpdateLoopRegion(TrackViewModel? changed)
    {
        var loops = Tracks
            .SelectMany(t => t.Regions.Where(r => r.Effect == RegionEffect.Loop).Select(r => (Track: t, Region: r)))
            .ToList();

        if (loops.Count > 1)
        {
            var keep = loops.LastOrDefault(x => ReferenceEquals(x.Track, changed) && ReferenceEquals(x.Region, x.Track.ActiveRegion));
            if (keep.Region is null) keep = loops[^1];
            foreach (var x in loops)
                if (!ReferenceEquals(x.Region, keep.Region)) x.Region.Effect = RegionEffect.None;
            loops = [keep];
        }

        if (loops.Count == 0)
        {
            _engine.ClearLoopRegion();
            _loopOwner = null;
            return;
        }

        var region = loops[0].Region;
        long start = MixerEngine.SecondsToFrames(region.StartSeconds);
        long end = MixerEngine.SecondsToFrames(region.EndSeconds);
        _engine.SetLoopRegion(start, end);

        // Vừa đặt vùng lặp mới mà playhead đang ngoài vùng thì nhảy về đầu vùng.
        if (!ReferenceEquals(_loopOwner, region))
        {
            _loopOwner = region;
            long position = _engine.PositionFrames;
            if (position < start || position >= end)
            {
                _engine.Seek(start);
                _stretch.Reset();
                Timeline.PositionSeconds = MixerEngine.FramesToSeconds(start);
            }
        }
    }

    // ---------- sắp xếp / xóa track ----------

    private void RenumberTracks()
    {
        for (int i = 0; i < Tracks.Count; i++) Tracks[i].Index = i + 1;
        _engine.SetStrips(Tracks.Select(t => t.Strip));
    }

    /// <summary>Đổi thứ tự một track (chỉ thứ tự hiển thị; track bị khóa không di chuyển được).</summary>
    public bool MoveTrack(int from, int to)
    {
        if (from < 0 || from >= Tracks.Count) return false;
        to = Math.Clamp(to, 0, Tracks.Count - 1);
        if (from == to || Tracks[from].IsLocked) return false;
        Tracks.Move(from, to);
        RenumberTracks();
        return true;
    }

    [RelayCommand]
    private void MoveTrackUp(TrackViewModel? track)
    {
        if (track is not null) MoveTrack(Tracks.IndexOf(track), Tracks.IndexOf(track) - 1);
    }

    [RelayCommand]
    private void MoveTrackDown(TrackViewModel? track)
    {
        if (track is not null) MoveTrack(Tracks.IndexOf(track), Tracks.IndexOf(track) + 1);
    }

    /// <summary>Xóa một track khỏi mix (file gốc trên đĩa không bị đụng tới). Track bị khóa không xóa được.</summary>
    [RelayCommand]
    private void RemoveTrack(TrackViewModel? track)
    {
        if (track is null || track.IsLocked || IsBusy || !Tracks.Remove(track)) return;

        RenumberTracks();
        Timeline.DurationSeconds = MixerEngine.FramesToSeconds(_engine.LengthFrames);
        if (Tracks.Count == 0)
        {
            _engine.Pause();
            IsPlaying = false;
            _engine.Seek(0);
            _stretch.Reset();
            Timeline.PositionSeconds = 0;
        }
        OverviewPeaks = BuildOverview(Tracks.Select(t => t.Strip.Track).ToList());
        UpdateLoopRegion(null);
        StartAnalysis();
        Status = $"Đã xóa track \"{track.Name}\".";
    }

    /// <summary>Thiết bị phát đang dùng (tự đổi khi bật/tắt tai nghe Bluetooth hoặc đổi thiết bị mặc định).</summary>
    [ObservableProperty]
    public partial string AudioDevice { get; set; } = "";

    private void HandleAudioDeviceChanged(string description, string? error)
    {
        // Sự kiện đến từ luồng nền của Windows: chuyển về luồng giao diện.
        Dispatcher.UIThread.Post(() =>
        {
            AudioDevice = description;
            AudioError = error is null ? "" : $"Không mở được thiết bị âm thanh: {error}";
        });
    }

    private void EnsureOutput()
    {
        if (!_enableAudioOutput || _output is not null || Tracks.Count == 0) return;
        try
        {
            _output = new AudioOutput(_stretch);
            AudioDevice = _output.Description;
            _output.DeviceChanged += HandleAudioDeviceChanged;
            AudioError = "";
        }
        catch (Exception ex)
        {
            // Giữ lỗi ở một ô riêng để không bị thông báo "Đã nạp ..." ghi đè.
            AudioError = $"Không mở được thiết bị âm thanh: {ex.Message}";
        }
    }

    // ---------- transport ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (Tracks.Count == 0) return;
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
        IsPlaying = _engine.IsPlaying;
    }

    /// <summary>Nút "back": về đầu bài, giữ nguyên trạng thái đang phát hay không.</summary>
    [RelayCommand]
    private void ToStart()
    {
        _engine.Seek(0);
        _stretch.Reset();
        Timeline.PositionSeconds = 0;
        Timeline.OffsetSeconds = 0;
    }

    /// <summary>Nút "stop": dừng và về đầu bài.</summary>
    [RelayCommand]
    private void Stop()
    {
        _engine.Pause();
        IsPlaying = false;
        ToStart();
    }

    private void OnSeekRequested(double seconds)
    {
        double clamped = Math.Clamp(seconds, 0, Timeline.DurationSeconds);
        _engine.Seek(MixerEngine.SecondsToFrames(clamped));
        _stretch.Reset();
        Timeline.PositionSeconds = clamped;
    }

    // ---------- zoom ----------

    [RelayCommand]
    private void ZoomIn() => Timeline.ZoomAround(1.25, Timeline.ViewportWidth / 2);

    [RelayCommand]
    private void ZoomOut() => Timeline.ZoomAround(1 / 1.25, Timeline.ViewportWidth / 2);

    [RelayCommand]
    private void ZoomFit() => Timeline.ZoomToFit();

    // ---------- xuất file ----------

    public async Task ExportAsync(string path, ExportFormat format)
    {
        if (IsBusy || Tracks.Count == 0) return;
        IsBusy = true;
        _engine.Pause();
        IsPlaying = false;
        try
        {
            var progress = new Progress<double>(p => Status = $"Đang xuất... {p:P0}");
            var result = await Task.Run(() => _engine.ExportWav(path, format, progress));
            Status = $"Đã xuất {Path.GetFileName(path)} ({MixerEngine.FramesToSeconds(result.Frames):0.0}s, đỉnh {result.PeakDbfs:0.0} dBFS)";
        }
        catch (Exception ex)
        {
            Status = $"Xuất thất bại: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- cập nhật định kỳ ----------

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.25, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        if (!IsBusy && IsPlaying != _engine.IsPlaying) IsPlaying = _engine.IsPlaying;

        double position = MixerEngine.FramesToSeconds(_engine.PositionFrames);
        Timeline.PositionSeconds = position;

        // Khi playhead chạy quá mép phải của vùng nhìn thì lật "trang".
        if (IsPlaying && Timeline.ViewportSeconds > 0 && position > Timeline.OffsetSeconds + Timeline.ViewportSeconds)
            Timeline.OffsetSeconds = position;

        foreach (var track in Tracks)
            track.UpdateMeters(dt);

        var (l, r) = _engine.MasterMeter.ReadAndReset();
        MasterMeterLeftDb = MeterBallistics.Apply(MasterMeterLeftDb, Decibels.FromLinear(l), dt);
        MasterMeterRightDb = MeterBallistics.Apply(MasterMeterRightDb, Decibels.FromLinear(r), dt);
    }

    public void Dispose()
    {
        _timer.Stop();
        _analysisCts?.Cancel();
        _engine.Pause();
        _output?.Dispose();
        _output = null;
    }
}
