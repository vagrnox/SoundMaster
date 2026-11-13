using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
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

    private static readonly Pen BarPen = new(new SolidColorBrush(Color.Parse("#000000"), 0.30), 1);
    private static readonly Pen BeatPen = new(new SolidColorBrush(Color.Parse("#000000"), 0.12), 1);

    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.Parse("#000000"), 0.45);
    private static readonly IBrush TitleBrush = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.92);
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

        // Khối clip (có khe hở nhỏ phía trên/dưới như ảnh mẫu).
        var clipRect = new Rect(x0, 3, x1 - x0, h - 6);
        context.DrawRectangle(TrackBrush ?? Brushes.Gray, null, clipRect, 4, 4);

        // Cột kẻ tối xuyên qua clip, giống ảnh mẫu.
        using (context.PushClip(clipRect))
        {
            foreach (var line in TimelineMath.GridLines(OffsetSeconds, pps, w, Bpm, BeatsPerBar))
            {
                double x = Math.Round(line.X) + 0.5;
                context.DrawLine(line.IsBar ? BarPen : BeatPen, new Point(x, clipRect.Top), new Point(x, clipRect.Bottom));
            }
        }

        DrawWaveform(context, track, pps, x0, x1, h);

        if (!string.IsNullOrEmpty(Title))
        {
            var text = new FormattedText(Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, TitleBrush);
            text.MaxTextWidth = Math.Max(1, clipRight - Math.Max(0, clipLeft) - 20);
            text.MaxTextHeight = 18;
            context.DrawText(text, new Point(Math.Max(10, clipLeft + 10), 8));
        }
    }

    private void DrawWaveform(DrawingContext context, AudioTrack track, double pps, double x0, double x1, double h)
    {
        double bucketSec = AudioTrack.PeakBucketFrames / (double)EngineFormat.SampleRate;
        double mid = h / 2;
        double maxAmp = h / 2 - 8;

        int firstPx = (int)Math.Floor(x0);
        int lastPx = (int)Math.Ceiling(x1);
        var tops = new List<Point>(lastPx - firstPx + 1);
        var bottoms = new List<Point>(lastPx - firstPx + 1);

        for (int x = firstPx; x < lastPx; x++)
        {
            double t0 = OffsetSeconds + x / pps;
            double t1 = OffsetSeconds + (x + 1) / pps;
            int b0 = (int)Math.Floor(t0 / bucketSec);
            int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(t1 / bucketSec));
            double peak = TimelineMath.MaxPeak(track.Peaks, b0, b1);
            double amp = Math.Max(0.75, peak * maxAmp);
            tops.Add(new Point(x, mid - amp));
            bottoms.Add(new Point(x, mid + amp));
        }

        if (tops.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tops[0], true);
            for (int i = 1; i < tops.Count; i++) g.LineTo(tops[i]);
            for (int i = bottoms.Count - 1; i >= 0; i--) g.LineTo(bottoms[i]);
            g.EndFigure(true);
        }

        context.DrawGeometry(WaveBrush, null, geometry);
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
