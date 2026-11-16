using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundMaster.App.ViewModels;

/// <summary>Hiệu ứng gắn với một vùng chọn trên track.</summary>
public enum RegionEffect
{
    None,
    FadeIn,
    FadeOut,
    Loop,
    Silence,
    Reverse,
}

/// <summary>Một vùng chọn (khoảng kéo thả) trên một track, kèm hiệu ứng (nếu có).</summary>
public partial class RegionViewModel : ViewModelBase
{
    /// <summary>Độ dài tối thiểu của một vùng (giây).</summary>
    public const double MinLengthSeconds = 0.05;

    public RegionViewModel(double startSeconds, double endSeconds)
    {
        StartSeconds = Math.Min(startSeconds, endSeconds);
        EndSeconds = Math.Max(startSeconds, endSeconds);
    }

    [ObservableProperty]
    public partial double StartSeconds { get; set; }

    [ObservableProperty]
    public partial double EndSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial RegionEffect Effect { get; set; }

    /// <summary>Hiệu ứng Loop: tổng số lần nghe đoạn (0 = lặp mãi cho tới khi gỡ), sau đó phát tiếp.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText))]
    public partial int LoopPlays { get; set; } = 4;

    public double LengthSeconds => EndSeconds - StartSeconds;

    /// <summary>Tên hiệu ứng hiện thành nhãn giữa vùng; rỗng nếu chưa có hiệu ứng.</summary>
    public string BadgeText => Effect switch
    {
        RegionEffect.FadeIn => "Fade in",
        RegionEffect.FadeOut => "Fade out",
        RegionEffect.Loop => LoopPlays <= 0 ? "Loop ∞" : $"Loop ×{LoopPlays}",
        RegionEffect.Silence => "Silence",
        RegionEffect.Reverse => "Reverse",
        _ => "",
    };

    public bool Contains(double seconds) => seconds >= StartSeconds && seconds <= EndSeconds;
}
