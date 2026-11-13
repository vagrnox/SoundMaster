using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Timeline;

namespace SoundMaster.App.ViewModels;

/// <summary>Trạng thái dùng chung của timeline: zoom, cuộn ngang, playhead, nhịp.</summary>
public partial class TimelineViewModel : ViewModelBase
{
    public const double MinPixelsPerSecond = 2;
    public const double MaxPixelsPerSecond = 400;
    public const int BeatsPerBar = 4;

    /// <summary>Yêu cầu tua tới một vị trí (giây); MainViewModel xử lý.</summary>
    public event Action<double>? SeekRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewportSeconds), nameof(ScrollMaximum))]
    public partial double PixelsPerSecond { get; set; } = 20;

    [ObservableProperty]
    public partial double OffsetSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClockText), nameof(BarsText))]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrollMaximum))]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText), nameof(BarsText))]
    public partial double Bpm { get; set; } = 120;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewportSeconds), nameof(ScrollMaximum))]
    public partial double ViewportWidth { get; set; }

    public double ViewportSeconds => PixelsPerSecond > 0 ? ViewportWidth / PixelsPerSecond : 0;
    public double ScrollMaximum => Math.Max(0, DurationSeconds - ViewportSeconds + 2);
    public string ClockText => TimelineMath.Clock(PositionSeconds);
    public string BarsText => TimelineMath.Bars(PositionSeconds, Bpm, BeatsPerBar);

    /// <summary>Ô nhập BPM: chỉ nhận giá trị 30–300.</summary>
    public string BpmText
    {
        get => Bpm.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm)
                && bpm is >= 30 and <= 300)
            {
                Bpm = bpm;
            }
            OnPropertyChanged();
        }
    }

    partial void OnPixelsPerSecondChanged(double value)
    {
        double clamped = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
        if (clamped != value)
        {
            PixelsPerSecond = clamped;
            return;
        }
        ReclampOffset();
    }

    partial void OnOffsetSecondsChanged(double value)
    {
        double clamped = Math.Clamp(value, 0, ScrollMaximum);
        if (clamped != value) OffsetSeconds = clamped;
    }

    partial void OnViewportWidthChanged(double value) => ReclampOffset();
    partial void OnDurationSecondsChanged(double value) => ReclampOffset();

    private void ReclampOffset()
    {
        double clamped = Math.Clamp(OffsetSeconds, 0, ScrollMaximum);
        if (clamped != OffsetSeconds) OffsetSeconds = clamped;
    }

    [RelayCommand]
    private void Seek(double seconds) => SeekRequested?.Invoke(Math.Max(0, seconds));

    /// <summary>Zoom giữ nguyên thời điểm đang nằm dưới <paramref name="anchorX"/> (pixel trong vùng lane).</summary>
    public void ZoomAround(double factor, double anchorX)
    {
        double anchorTime = OffsetSeconds + anchorX / PixelsPerSecond;
        PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
        OffsetSeconds = Math.Max(0, anchorTime - anchorX / PixelsPerSecond);
    }

    public void ZoomToFit()
    {
        if (ViewportWidth <= 0 || DurationSeconds <= 0) return;
        PixelsPerSecond = Math.Clamp((ViewportWidth - 24) / DurationSeconds, MinPixelsPerSecond, MaxPixelsPerSecond);
        OffsetSeconds = 0;
    }
}
