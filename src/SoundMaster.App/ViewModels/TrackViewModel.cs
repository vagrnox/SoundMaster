using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    /// <summary>Nền lane: xám khi bị khóa, sáng lên khi đang chọn, trong suốt khi bình thường.</summary>
    public IBrush LaneBackground => IsSelected
        ? new SolidColorBrush(Color.Parse(IsLocked ? "#2A2A2A" : "#18202A"))
        : IsLocked ? new SolidColorBrush(Color.Parse("#1C1C1C")) : Brushes.Transparent;

    /// <summary>Nền hàng header trong sidebar: sáng hơn một chút khi track đang chọn.</summary>
    public IBrush HeaderBackground => new SolidColorBrush(Color.Parse(IsSelected ? "#20252B" : "#141414"));

    /// <summary>Track đang được chọn (bấm vào lane hoặc header). Chỉ một track được chọn tại một thời điểm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaneBackground), nameof(HeaderBackground))]
    public partial bool IsSelected { get; set; }

    // ---------- vùng chọn & hiệu ứng ----------

    /// <summary>Các vùng chọn trên track (vùng có hiệu ứng được giữ lại cùng nhãn).</summary>
    public ObservableCollection<RegionViewModel> Regions { get; } = [];

    /// <summary>Vùng đang chọn: viền vàng, phần ngoài vùng của track chuyển xám.</summary>
    [ObservableProperty]
    public partial RegionViewModel? ActiveRegion { get; set; }

    /// <summary>Yêu cầu chọn track này (MainViewModel bỏ chọn các track khác).</summary>
    public event Action<TrackViewModel>? SelectRequested;

    /// <summary>Vùng hoặc hiệu ứng thay đổi (MainViewModel cập nhật vùng lặp).</summary>
    public event Action<TrackViewModel>? RegionsChanged;

    public void RequestSelect() => SelectRequested?.Invoke(this);

    public double DurationSeconds => Audio.Duration.TotalSeconds;

    /// <summary>Vùng (mới nhất trước) chứa thời điểm <paramref name="seconds"/>, hoặc null.</summary>
    public RegionViewModel? RegionAt(double seconds)
    {
        for (int i = Regions.Count - 1; i >= 0; i--)
            if (Regions[i].Contains(seconds)) return Regions[i];
        return null;
    }

    /// <summary>Tạo vùng mới và đặt làm vùng đang chọn.</summary>
    public RegionViewModel BeginRegion(double fromSeconds, double toSeconds)
    {
        double a = Math.Clamp(Math.Min(fromSeconds, toSeconds), 0, DurationSeconds);
        double b = Math.Clamp(Math.Max(fromSeconds, toSeconds), 0, DurationSeconds);
        var region = new RegionViewModel(a, b);
        Regions.Add(region);
        ActiveRegion = region;
        return region;
    }

    /// <summary>Bỏ chọn vùng hiện tại; vùng chưa có hiệu ứng thì xóa luôn.</summary>
    public void DeactivateRegion()
    {
        if (ActiveRegion is { Effect: RegionEffect.None } empty) Regions.Remove(empty);
        ActiveRegion = null;
    }

    public void ActivateRegion(RegionViewModel region)
    {
        if (ReferenceEquals(ActiveRegion, region)) return;
        if (ActiveRegion is { Effect: RegionEffect.None } empty) Regions.Remove(empty);
        ActiveRegion = region;
    }

    /// <summary>Đặt hiệu ứng cho vùng (None = bỏ hiệu ứng). Track bị khóa thì bỏ qua.</summary>
    public void SetEffect(RegionViewModel region, RegionEffect effect)
    {
        if (IsLocked || !Regions.Contains(region)) return;
        region.Effect = effect;
        NotifyRegionsChanged();
    }

    /// <summary>Đặt hiệu ứng Loop với tổng số lần nghe (0 = lặp mãi).</summary>
    public void SetLoop(RegionViewModel region, int plays)
    {
        if (IsLocked || !Regions.Contains(region)) return;
        region.LoopPlays = Math.Max(0, plays);
        region.Effect = RegionEffect.Loop;
        NotifyRegionsChanged();
    }

    public void RemoveRegion(RegionViewModel region)
    {
        if (IsLocked || !Regions.Remove(region)) return;
        if (ReferenceEquals(ActiveRegion, region)) ActiveRegion = null;
        NotifyRegionsChanged();
    }

    /// <summary>Gọi sau khi vùng đổi (kéo cạnh, đổi hiệu ứng): dựng lại hiệu ứng cho engine và báo MainViewModel.</summary>
    public void NotifyRegionsChanged()
    {
        long total = Audio.FrameCount;
        Strip.SetEffects(Regions
            .Select(r => (Region: r, Kind: ToKind(r.Effect)))
            .Where(x => x.Kind is not null)
            .Select(x => new ClipEffect(
                x.Kind!.Value,
                Math.Clamp(MixerEngine.SecondsToFrames(x.Region.StartSeconds), 0, total),
                Math.Clamp(MixerEngine.SecondsToFrames(x.Region.EndSeconds), 0, total)))
            .Where(e => e.EndFrame > e.StartFrame));
        RegionsChanged?.Invoke(this);
    }

    private static ClipEffectKind? ToKind(RegionEffect effect) => effect switch
    {
        RegionEffect.FadeIn => ClipEffectKind.FadeIn,
        RegionEffect.FadeOut => ClipEffectKind.FadeOut,
        RegionEffect.Silence => ClipEffectKind.Silence,
        RegionEffect.Reverse => ClipEffectKind.Reverse,
        _ => null, // None và Loop không phải hiệu ứng trên stem (Loop là của transport)
    };

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
