using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Timeline;

namespace SoundMaster.App.Controls;

/// <summary>
/// Một lane: nền màu theo track + waveform đối xứng. Chỉ vẽ phần đang nhìn thấy
/// (theo OffsetSeconds / PixelsPerSecond). Bấm hoặc kéo trên lane để tua.
/// </summary>
public class WaveformView : Control
{
    public static readonly StyledProperty<AudioTrack?> TrackProperty =
        AvaloniaProperty.Register<WaveformView, AudioTrack?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WaveformView, string?>(nameof(Title));

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(PixelsPerSecond), 20);

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<WaveformView, ICommand?>(nameof(SeekCommand));

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<int> BeatsPerBarProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(BeatsPerBar), 4);


    private static readonly IBrush Transparent = Brushes.Transparent;

    private bool _dragging;

    static WaveformView()
    {
        AffectsRender<WaveformView>(TrackProperty, TrackBrushProperty, TitleProperty, PixelsPerSecondProperty,
            OffsetSecondsProperty, BpmProperty, BeatsPerBarProperty);
    }

    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public int BeatsPerBar { get => GetValue(BeatsPerBarProperty); set => SetValue(BeatsPerBarProperty, value); }

    public AudioTrack? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Nền trong suốt để lane nhận được chuột ngay cả ngoài vùng clip.
        context.DrawRectangle(Transparent, null, new Rect(0, 0, w, h));

        var track = Track;
        double pps = PixelsPerSecond;
        if (track is null || pps <= 0 || w <= 0 || track.FrameCount == 0) return;

        double duration = track.Duration.TotalSeconds;
        double clipLeft = (0 - OffsetSeconds) * pps;
        double clipRight = (duration - OffsetSeconds) * pps;
        double x0 = Math.Max(0, clipLeft);
        double x1 = Math.Min(w, clipRight);
        if (x1 <= x0) return;

        DrawBars(context, track, pps, x0, x1, h);
    }

    /// <summary>
    /// Waveform dạng cột đứng mảnh (rộng 2 px, cách 4 px) theo màu track, đối xứng quanh trục giữa.
    /// Cột được căn theo tọa độ thế giới nên khi cuộn các cột không nhấp nháy.
    /// </summary>
    private void DrawBars(DrawingContext context, AudioTrack track, double pps, double x0, double x1, double h)
    {
        const double step = 4, barWidth = 2;
        double bucketSec = AudioTrack.PeakBucketFrames / (double)EngineFormat.SampleRate;
        double mid = h / 2;
        double maxAmp = h / 2 - 8;
        var brush = TrackBrush ?? Brushes.Gray;

        double worldLeft = OffsetSeconds * pps;
        int firstCol = (int)Math.Floor((worldLeft + x0) / step);
        int lastCol = (int)Math.Ceiling((worldLeft + x1) / step);

        for (int c = firstCol; c < lastCol; c++)
        {
            double x = c * step - worldLeft;
            double t0 = c * step / pps;
            double t1 = (c + 1) * step / pps;
            int b0 = (int)Math.Floor(t0 / bucketSec);
            int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(t1 / bucketSec));
            double peak = TimelineMath.MaxPeak(track.Peaks, b0, b1);

            // Im lặng vẫn vẽ chấm 1 px để thành đường đứt nét ở giữa, như ảnh mẫu.
            double amp = Math.Max(0.5, peak * maxAmp);
            context.DrawRectangle(brush, null, new Rect(x, mid - amp, barWidth, amp * 2), 1, 1);
        }
    }

    // ---------- tua bằng chuột ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Seek(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Seek(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Seek(PointerEventArgs e)
    {
        if (PixelsPerSecond <= 0) return;
        double seconds = Math.Max(0, OffsetSeconds + e.GetPosition(this).X / PixelsPerSecond);
        SeekCommand?.Execute(seconds);
    }
}
