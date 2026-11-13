using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.ViewModels;

public partial class StripViewModel : ViewModelBase
{
    public StripViewModel(ChannelStrip strip)
    {
        Strip = strip;
        Name = strip.Name;
    }

    public ChannelStrip Strip { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    public partial double GainDb { get; set; }

    [ObservableProperty]
    public partial double Pan { get; set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsSolo { get; set; }

    [ObservableProperty]
    public partial double MeterLeftDb { get; set; } = -90;

    [ObservableProperty]
    public partial double MeterRightDb { get; set; } = -90;

    public string GainText => GainDb <= ChannelStrip.MinGainDb ? "-∞ dB" : $"{GainDb:0.0} dB";

    partial void OnNameChanged(string value) => Strip.Name = value;
    partial void OnGainDbChanged(double value) => Strip.GainDb = (float)value;
    partial void OnPanChanged(double value) => Strip.Pan = (float)value;
    partial void OnIsMutedChanged(bool value) => Strip.Muted = value;
    partial void OnIsSoloChanged(bool value) => Strip.Soloed = value;

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
