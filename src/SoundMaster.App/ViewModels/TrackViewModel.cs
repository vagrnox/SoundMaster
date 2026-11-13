using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.ViewModels;

public static class TrackPalette
{
    public static readonly string[] Colors =
    [
        "#A3195B", "#0B8A3E", "#1565C0", "#D95F02", "#9A8A12",
        "#6A1B9A", "#00838F", "#C62828", "#5D4037", "#546E7A",
    ];

    public static string ForIndex(int index) => Colors[index % Colors.Length];
}

public partial class ColorSwatch : ObservableObject
{
    private readonly Action<string> _apply;

    public ColorSwatch(string hex, Action<string> apply)
    {
        Hex = hex;
        Brush = new SolidColorBrush(Color.Parse(hex));
        _apply = apply;
    }

    public string Hex { get; }
    public IBrush Brush { get; }

    [RelayCommand]
    private void Apply() => _apply(Hex);
}

public partial class TrackViewModel : ViewModelBase
{
    public const double RowHeight = 68;

    public TrackViewModel(ChannelStrip strip, TimelineViewModel timeline, int index, string colorHex)
    {
        Strip = strip;
        Timeline = timeline;
        Index = index;
        Name = strip.Name;
        ColorHex = colorHex;
        Swatches = TrackPalette.Colors
            .Select(hex => new ColorSwatch(hex, h =>
            {
                ColorHex = h;
                IsColorPickerOpen = false;
            }))
            .ToList();
    }

    public ChannelStrip Strip { get; }
    public AudioTrack Audio => Strip.Track;
    public TimelineViewModel Timeline { get; }
    public IReadOnlyList<ColorSwatch> Swatches { get; }

    [ObservableProperty]
    public partial int Index { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorBrush), nameof(ColorValue))]
    public partial string ColorHex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    public partial double GainDb { get; set; }

    [ObservableProperty]
    public partial double Pan { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSolo { get; set; }

    /// <summary>Trạng thái arm (sẵn sàng ghi). Chưa có ghi âm nên hiện chỉ lưu cờ.</summary>
    [ObservableProperty]
    public partial bool IsArmed { get; set; }

    /// <summary>Khóa track: không cho sửa tên, màu, volume, pan, mute, solo, arm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(ColorBrush), nameof(ColorValue), nameof(LaneBackground))]
    public partial bool IsLocked { get; set; }

    [ObservableProperty]
    public partial bool IsColorPickerOpen { get; set; }

    /// <summary>Mức tín hiệu sau fader (dB), hiển thị trong thanh volume.</summary>
    [ObservableProperty]
    public partial double MeterLeftDb { get; set; } = -90;

    [ObservableProperty]
    public partial double MeterRightDb { get; set; } = -90;

    public bool IsEditable => !IsLocked;
    /// <summary>Màu khi bị khóa: xám (mọi thanh của track chuyển xám).</summary>
    public static readonly Color LockedColor = Color.Parse("#6B6B6B");

    public IBrush ColorBrush => new SolidColorBrush(ColorValue);
    /// <summary>Nền lane: xám như sidebar khi track bị khóa, trong suốt khi không.</summary>
    public IBrush LaneBackground => IsLocked ? new SolidColorBrush(Color.Parse("#1C1C1C")) : Brushes.Transparent;

    public Color ColorValue => IsLocked ? LockedColor : Color.Parse(ColorHex);
    public string GainText => GainDb <= ChannelStrip.MinGainDb ? "Volume: -∞ dB" : $"Volume: {GainDb:0.0} dB";

    partial void OnNameChanged(string value) => Strip.Name = value;
    partial void OnGainDbChanged(double value) => Strip.GainDb = (float)value;
    partial void OnPanChanged(double value) => Strip.Pan = (float)value;
    partial void OnIsMutedChanged(bool value) => Strip.Muted = value;
    partial void OnIsSoloChanged(bool value) => Strip.Soloed = value;

    partial void OnIsLockedChanged(bool value)
    {
        if (value) IsColorPickerOpen = false;
    }

    [RelayCommand]
    private void OpenColorPicker() => IsColorPickerOpen = true;

    public void UpdateMeters(double dtSeconds)
    {
        var (l, r) = Strip.Meter.ReadAndReset();
        MeterLeftDb = MeterBallistics.Apply(MeterLeftDb, Decibels.FromLinear(l), dtSeconds);
        MeterRightDb = MeterBallistics.Apply(MeterRightDb, Decibels.FromLinear(r), dtSeconds);
    }
}

/// <summary>Lên tức thì, rơi 30 dB/giây.</summary>
public static class MeterBallistics
{
    public static double Apply(double current, double incomingDb, double dtSeconds)
        => incomingDb > current ? incomingDb : Math.Max(incomingDb, current - 30.0 * dtSeconds);
}
