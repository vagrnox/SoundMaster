using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SoundMaster.App.Controls;

/// <summary>Đồng hồ mức stereo (hai cột L/R) vẽ trực tiếp, thang -60 … +6 dB.</summary>
public class LevelMeter : Control
{
    public const double MinDb = -60;
    public const double MaxDb = 6;

    public static readonly StyledProperty<double> LeftDbProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(LeftDb), -90);

    public static readonly StyledProperty<double> RightDbProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(RightDb), -90);

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#1A1D23"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#F5C542"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF5252"));
    private static readonly IBrush Tick = new SolidColorBrush(Color.Parse("#40FFFFFF"));

    static LevelMeter()
    {
        AffectsRender<LevelMeter>(LeftDbProperty, RightDbProperty);
    }

    public double LeftDb
    {
        get => GetValue(LeftDbProperty);
        set => SetValue(LeftDbProperty, value);
    }

    public double RightDb
    {
        get => GetValue(RightDbProperty);
        set => SetValue(RightDbProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Background, null, bounds, 2, 2);

        double gap = 2;
        double barWidth = (bounds.Width - gap * 3) / 2;
        DrawBar(context, gap, barWidth, bounds.Height, LeftDb);
        DrawBar(context, gap * 2 + barWidth, barWidth, bounds.Height, RightDb);

        foreach (double db in new[] { 0.0, -12.0, -24.0, -48.0 })
        {
            double y = DbToY(db, bounds.Height);
            context.DrawLine(new Pen(Tick, 1), new Point(0, y), new Point(bounds.Width, y));
        }
    }

    private static void DrawBar(DrawingContext context, double x, double width, double height, double db)
    {
        double top = DbToY(db, height);
        // Vẽ theo ba vùng màu: xanh đến -12 dB, vàng đến -3 dB, đỏ phía trên.
        DrawSegment(context, x, width, height, top, MinDb, -12, Green);
        DrawSegment(context, x, width, height, top, -12, -3, Yellow);
        DrawSegment(context, x, width, height, top, -3, MaxDb, Red);
    }

    private static void DrawSegment(DrawingContext context, double x, double width, double height,
        double levelTop, double fromDb, double toDb, IBrush brush)
    {
        double segTop = DbToY(toDb, height);
        double segBottom = DbToY(fromDb, height);
        double top = System.Math.Max(segTop, levelTop);
        if (top >= segBottom) return;
        context.DrawRectangle(brush, null, new Rect(x, top, width, segBottom - top));
    }

    private static double DbToY(double db, double height)
    {
        double clamped = System.Math.Clamp(db, MinDb, MaxDb);
        return height * (1 - (clamped - MinDb) / (MaxDb - MinDb));
    }
}
