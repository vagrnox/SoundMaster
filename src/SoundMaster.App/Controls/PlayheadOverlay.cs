using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SoundMaster.App.Controls;

/// <summary>Vạch playhead phủ lên toàn bộ vùng timeline; không nhận chuột.</summary>
public class PlayheadOverlay : Control
{
    public static readonly StyledProperty<double> PositionSecondsProperty =
        AvaloniaProperty.Register<PlayheadOverlay, double>(nameof(PositionSeconds));

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<PlayheadOverlay, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<PlayheadOverlay, double>(nameof(PixelsPerSecond), 20);

    private static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#FFFFFF"), 0.9);
    private static readonly Pen LinePen = new(LineBrush, 1.5);

    static PlayheadOverlay()
    {
        AffectsRender<PlayheadOverlay>(PositionSecondsProperty, OffsetSecondsProperty, PixelsPerSecondProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<PlayheadOverlay>(false);
    }

    public double PositionSeconds { get => GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }

    public override void Render(DrawingContext context)
    {
        double x = (PositionSeconds - OffsetSeconds) * PixelsPerSecond;
        if (x < 0 || x > Bounds.Width) return;

        context.DrawLine(LinePen, new Point(x, 0), new Point(x, Bounds.Height));
        context.DrawEllipse(LineBrush, null, new Point(x, 9), 5, 5);
    }
}
