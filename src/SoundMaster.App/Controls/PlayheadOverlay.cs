using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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
    private static readonly ImmutableSolidColorBrush LineBrush = new ImmutableSolidColorBrush(Color.Parse("#F1AB29"));
    private static readonly IPen LinePen = new ImmutablePen(LineBrush, 1.5);

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
        // Đầu playhead: tam giác đỉnh chỉ xuống track.
        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(x - 6, 0), true);
            g.LineTo(new Point(x + 6, 0));
            g.LineTo(new Point(x, 11));
            g.EndFigure(true);
        }
        context.DrawGeometry(LineBrush, null, tri);
    }
}
