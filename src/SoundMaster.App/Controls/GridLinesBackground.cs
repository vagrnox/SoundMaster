using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SoundMaster.Core.Timeline;

namespace SoundMaster.App.Controls;

/// <summary>Nền của vùng lane: các cột kẻ dọc theo ô nhịp / phách. Không nhận chuột.</summary>
public class GridLinesBackground : Control
{
    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<GridLinesBackground, double>(nameof(PixelsPerSecond), 20);

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<GridLinesBackground, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<GridLinesBackground, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<int> BeatsPerBarProperty =
        AvaloniaProperty.Register<GridLinesBackground, int>(nameof(BeatsPerBar), 4);

    private static readonly IBrush Back = new SolidColorBrush(Color.Parse("#121212"));
    private static readonly Pen BarPen = new(new SolidColorBrush(Color.Parse("#FFFFFF"), 0.10), 1);
    private static readonly Pen BeatPen = new(new SolidColorBrush(Color.Parse("#FFFFFF"), 0.045), 1);

    static GridLinesBackground()
    {
        AffectsRender<GridLinesBackground>(PixelsPerSecondProperty, OffsetSecondsProperty, BpmProperty, BeatsPerBarProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GridLinesBackground>(false);
    }

    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public int BeatsPerBar { get => GetValue(BeatsPerBarProperty); set => SetValue(BeatsPerBarProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        context.DrawRectangle(Back, null, new Rect(0, 0, w, h));

        foreach (var line in TimelineMath.GridLines(OffsetSeconds, PixelsPerSecond, w, Bpm, BeatsPerBar))
        {
            double x = System.Math.Round(line.X) + 0.5; // căn theo pixel cho nét mảnh
            context.DrawLine(line.IsBar ? BarPen : BeatPen, new Point(x, 0), new Point(x, h));
        }
    }
}
