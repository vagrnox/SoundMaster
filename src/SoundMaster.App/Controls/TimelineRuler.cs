using System;
using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.Core.Timeline;

namespace SoundMaster.App.Controls;

public enum RulerMode { Bars, Time }

/// <summary>
/// Thước timeline chỉ vẽ phần đang nhìn thấy (theo OffsetSeconds / PixelsPerSecond),
/// nên không phải tạo control rộng hàng chục nghìn pixel. Bấm hoặc kéo để tua.
/// </summary>
public class TimelineRuler : Control
{
    public static readonly StyledProperty<RulerMode> ModeProperty =
        AvaloniaProperty.Register<TimelineRuler, RulerMode>(nameof(Mode));

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<TimelineRuler, double>(nameof(PixelsPerSecond), 20);

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<TimelineRuler, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<TimelineRuler, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<int> BeatsPerBarProperty =
        AvaloniaProperty.Register<TimelineRuler, int>(nameof(BeatsPerBar), 4);

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<TimelineRuler, ICommand?>(nameof(SeekCommand));

    private static readonly IBrush Back = new ImmutableSolidColorBrush(Color.Parse("#0E0E0E"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly ImmutablePen MajorPen = new(new ImmutableSolidColorBrush(Color.Parse("#7A7A7A")), 1);
    private static readonly ImmutablePen MinorPen = new(new ImmutableSolidColorBrush(Color.Parse("#444444")), 1);

    private bool _dragging;

    static TimelineRuler()
    {
        AffectsRender<TimelineRuler>(ModeProperty, PixelsPerSecondProperty, OffsetSecondsProperty, BpmProperty, BeatsPerBarProperty);
    }

    public RulerMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public int BeatsPerBar { get => GetValue(BeatsPerBarProperty); set => SetValue(BeatsPerBarProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        context.DrawRectangle(Back, null, new Rect(0, 0, w, h));

        double pps = PixelsPerSecond;
        if (pps <= 0 || w <= 0) return;

        if (Mode == RulerMode.Bars) RenderBars(context, w, h, pps);
        else RenderTime(context, w, h, pps);
    }

    private void RenderBars(DrawingContext context, double w, double h, double pps)
    {
        double bpm = Bpm, beatsPerBar = Math.Max(1, BeatsPerBar);
        if (bpm <= 0) return;

        double beatSec = 60.0 / bpm;
        double barSec = beatSec * beatsPerBar;
        double barPx = barSec * pps, beatPx = beatSec * pps;

        int labelEvery = 1;
        while (labelEvery * barPx < 44 && labelEvery < 1 << 20) labelEvery *= 2;

        long firstBar = Math.Max(0, (long)Math.Floor(OffsetSeconds / barSec));
        long lastBar = (long)Math.Ceiling((OffsetSeconds + w / pps) / barSec);

        for (long bar = firstBar; bar <= lastBar; bar++)
        {
            bool major = bar % labelEvery == 0;
            double x = (bar * barSec - OffsetSeconds) * pps;

            if (major || barPx >= 8)
                context.DrawLine(major ? MajorPen : MinorPen, new Point(x, h * 0.45), new Point(x, h));

            if (major)
                DrawLabel(context, (bar + 1).ToString(CultureInfo.InvariantCulture), x + 4, 4);

            if (barPx >= 8 && beatPx >= 16)
            {
                for (int beat = 1; beat < beatsPerBar; beat++)
                {
                    double bx = x + beat * beatPx;
                    context.DrawLine(MinorPen, new Point(bx, h * 0.72), new Point(bx, h));
                }
            }
        }
    }

    private void RenderTime(DrawingContext context, double w, double h, double pps)
    {
        double step = TimelineMath.NiceTimeStep(pps);
        double minorPx = step / 4 * pps;

        long first = Math.Max(0, (long)Math.Floor(OffsetSeconds / step));
        long last = (long)Math.Ceiling((OffsetSeconds + w / pps) / step);

        for (long i = first; i <= last; i++)
        {
            double t = i * step;
            double x = (t - OffsetSeconds) * pps;
            context.DrawLine(MajorPen, new Point(x, 0), new Point(x, h * 0.4));
            DrawLabel(context, TimelineMath.RulerLabel(t, step), x + 4, h * 0.38);

            if (minorPx >= 8)
            {
                for (int k = 1; k < 4; k++)
                {
                    double mx = x + k * minorPx;
                    context.DrawLine(MinorPen, new Point(mx, 0), new Point(mx, h * 0.22));
                }
            }
        }
    }

    private static void DrawLabel(DrawingContext context, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 11, LabelBrush);
        context.DrawText(formatted, new Point(x, y));
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
