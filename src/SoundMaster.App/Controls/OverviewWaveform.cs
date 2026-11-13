using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SoundMaster.App.Controls;

/// <summary>
/// Waveform tổng quan toàn bài (một đường bao đã gộp các track) kèm vị trí đang phát.
/// Bấm hoặc kéo để tua; phần đã phát sáng hơn phần chưa phát.
/// </summary>
public class OverviewWaveform : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty =
        AvaloniaProperty.Register<OverviewWaveform, float[]?>(nameof(Peaks));

    public static readonly StyledProperty<double> PositionSecondsProperty =
        AvaloniaProperty.Register<OverviewWaveform, double>(nameof(PositionSeconds));

    public static readonly StyledProperty<double> DurationSecondsProperty =
        AvaloniaProperty.Register<OverviewWaveform, double>(nameof(DurationSeconds));

    public static readonly StyledProperty<ICommand?> SeekCommandProperty =
        AvaloniaProperty.Register<OverviewWaveform, ICommand?>(nameof(SeekCommand));

    private static readonly IBrush PlayedBrush = new ImmutableSolidColorBrush(Color.Parse("#F1AB29"), 0.85);
    private static readonly IBrush RestBrush = new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.28);
    private static readonly ImmutablePen CenterPen = new(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.12), 1);

    static OverviewWaveform()
    {
        AffectsRender<OverviewWaveform>(PeaksProperty, PositionSecondsProperty, DurationSecondsProperty);
        CursorProperty.OverrideDefaultValue<OverviewWaveform>(new Cursor(StandardCursorType.Hand));
    }

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double PositionSeconds { get => GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }
    public double DurationSeconds { get => GetValue(DurationSecondsProperty); set => SetValue(DurationSecondsProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Vùng bấm phủ toàn bộ control, kể cả khi chưa có dữ liệu.
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double mid = h / 2;
        context.DrawLine(CenterPen, new Point(0, mid), new Point(w, mid));

        var peaks = Peaks;
        if (peaks is null || peaks.Length == 0 || w < 1) return;

        double playedX = DurationSeconds > 0 ? Math.Clamp(PositionSeconds / DurationSeconds, 0, 1) * w : 0;
        int columns = (int)Math.Floor(w / 3); // mỗi cột 2 px + khe 1 px
        for (int c = 0; c < columns; c++)
        {
            double x = c * 3;
            int from = (int)((long)c * peaks.Length / columns);
            int to = Math.Max(from + 1, (int)((long)(c + 1) * peaks.Length / columns));
            float max = 0;
            for (int i = from; i < to && i < peaks.Length; i++) if (peaks[i] > max) max = peaks[i];

            double half = Math.Max(1, max * (h / 2 - 1));
            context.DrawRectangle(x + 1 <= playedX ? PlayedBrush : RestBrush, null,
                new Rect(x, mid - half, 2, half * 2), 1, 1);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        Seek(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this) Seek(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
    }

    private void Seek(PointerEventArgs e)
    {
        if (Bounds.Width <= 0 || DurationSeconds <= 0) return;
        double x = Math.Clamp(e.GetPosition(this).X, 0, Bounds.Width);
        SeekCommand?.Execute(x / Bounds.Width * DurationSeconds);
    }
}
