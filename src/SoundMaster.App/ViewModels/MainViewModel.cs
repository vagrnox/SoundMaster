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
    private bool _updatingFromEngine;
    private DateTime _lastTick = DateTime.UtcNow;

    public MainViewModel()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public ObservableCollection<StripViewModel> Strips { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "Mở thư mục stems, thêm file, hoặc nạp stem demo để thử.";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseText))]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MasterGainText))]
    public partial double MasterGainDb { get; set; }

    [ObservableProperty]
    public partial bool LimiterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool LoopEnabled { get; set; }

    [ObservableProperty]
    public partial double MasterMeterLeftDb { get; set; } = -90;

    [ObservableProperty]
    public partial double MasterMeterRightDb { get; set; } = -90;

    [ObservableProperty]
    public partial double LimiterReductionDb { get; set; }

    public string PlayPauseText => IsPlaying ? "⏸ Pause" : "▶ Play";
    public string MasterGainText => MasterGainDb <= MixerEngine.MasterMinDb ? "-∞ dB" : $"{MasterGainDb:0.0} dB";
    public string TimeText => $"{FormatTime(PositionSeconds)} / {FormatTime(DurationSeconds)}";
    public double SeekMaximum => Math.Max(1, DurationSeconds);

    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeText));
        if (!_updatingFromEngine)
            _engine.Seek(MixerEngine.SecondsToFrames(value));
    }

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(SeekMaximum));
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
                ? $"Đã nạp {loaded.Count} stem."
                : $"Đã nạp {loaded.Count} stem, lỗi {errors.Count}: {string.Join("; ", errors)}";
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
        Status = "Đã xóa các stem.";
    }

    private void ApplyTracks(IReadOnlyList<AudioTrack> tracks, bool replace)
    {
        _engine.Pause();
        if (replace) Strips.Clear();
        foreach (var track in tracks)
            Strips.Add(new StripViewModel(new ChannelStrip(track)));

        _engine.SetStrips(Strips.Select(s => s.Strip));
        DurationSeconds = MixerEngine.FramesToSeconds(_engine.LengthFrames);
        if (replace) SetPosition(0);
        EnsureOutput();
    }

    private void EnsureOutput()
    {
        if (_output is not null || Strips.Count == 0) return;
        try
        {
            _output = new AudioOutput(_engine);
        }
        catch (Exception ex)
        {
            Status = $"Không mở được thiết bị âm thanh: {ex.Message}";
        }
    }

    // ---------- transport ----------

    [RelayCommand]
    private void PlayPause()
    {
        if (Strips.Count == 0) return;
        if (_engine.IsPlaying) _engine.Pause();
        else _engine.Play();
        IsPlaying = _engine.IsPlaying;
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        IsPlaying = false;
        SetPosition(0);
    }

    // ---------- xuất file ----------

    public async Task ExportAsync(string path, ExportFormat format)
    {
        if (IsBusy || Strips.Count == 0) return;
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

    private void SetPosition(double seconds)
    {
        _updatingFromEngine = true;
        PositionSeconds = seconds;
        _updatingFromEngine = false;
    }

    private void OnTick()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.25, (now - _lastTick).TotalSeconds);
        _lastTick = now;

        if (IsPlaying != _engine.IsPlaying && !IsBusy) IsPlaying = _engine.IsPlaying;
        SetPosition(MixerEngine.FramesToSeconds(_engine.PositionFrames));

        foreach (var strip in Strips)
            strip.UpdateMeters(dt);

        var (l, r) = _engine.MasterMeter.ReadAndReset();
        MasterMeterLeftDb = MeterBallistics.Apply(MasterMeterLeftDb, Decibels.FromLinear(l), dt);
        MasterMeterRightDb = MeterBallistics.Apply(MasterMeterRightDb, Decibels.FromLinear(r), dt);
        LimiterReductionDb = _engine.Limiter.GainReductionDb;
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    public void Dispose()
    {
        _timer.Stop();
        _engine.Pause();
        _output?.Dispose();
        _output = null;
    }
}
