using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] AudioExtensions = [".wav", ".mp3", ".aiff", ".aif", ".m4a", ".wma"];

    private readonly MixerEngine _engine = new();
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
        Timeline.SeekRequested += OnSeekRequested;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public TimelineViewModel Timeline { get; } = new();
    public ObservableCollection<TrackViewModel> Tracks { get; } = [];

    [ObservableProperty]
    public partial string SongName { get; set; } = "Untitled";

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
    [NotifyPropertyChangedFor(nameof(IsMasterEditable))]
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
            Tracks.Add(new TrackViewModel(new ChannelStrip(track), Timeline, index + 1, TrackPalette.ForIndex(index)));
        }

        _engine.SetStrips(Tracks.Select(t => t.Strip));
        Timeline.DurationSeconds = MixerEngine.FramesToSeconds(_engine.LengthFrames);
        if (replace)
        {
            Timeline.PositionSeconds = 0;
            _engine.Seek(0);
            Timeline.ZoomToFit();
        }
        EnsureOutput();
    }

    private void EnsureOutput()
    {
        if (!_enableAudioOutput || _output is not null || Tracks.Count == 0) return;
        try
        {
            _output = new AudioOutput(_engine);
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
        Timeline.PositionSeconds = 0;
        Timeline.OffsetSeconds = 0;
    }

    private void OnSeekRequested(double seconds)
    {
        double clamped = Math.Clamp(seconds, 0, Timeline.DurationSeconds);
        _engine.Seek(MixerEngine.SecondsToFrames(clamped));
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
        _engine.Pause();
        _output?.Dispose();
        _output = null;
    }
}
