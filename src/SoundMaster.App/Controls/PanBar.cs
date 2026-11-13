using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SoundMaster.App.Controls;

/// <summary>
/// Ô balance L/R dạng viên thuốc nền đen cùng kiểu <see cref="VolumeBar"/>: vạch xanh là vị trí pan
/// (-1 = trái hết, +1 = phải hết, kéo hoặc bấm để đặt, double-click về giữa). Hai bên có gradient màu
/// <see cref="AccentColor"/> mờ; kéo về phía nào thì gradient phía đó rõ lên.
/// </summary>
public class PanBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PanBar, double>(nameof(Value), 0.0,
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(v, -1, 1));

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<PanBar, Color>(nameof(AccentColor), Color.Parse("#3DDC84"));

    private const double IdleAlpha = 0.16;   // độ rõ của gradient khi chưa lệch
    private const double ActiveAlpha = 0.95; // độ rõ khi kéo hết về phía đó

    private static readonly IBrush PillBrush = new ImmutableSolidColorBrush(Color.Parse("#000000"));
    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2E2E2E")), 1);
    private static readonly IPen CenterPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.22), 1);
    private static readonly IBrush ThumbBrush = new ImmutableSolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.55);

    private bool _dragging;

    static PanBar()
    {
        AffectsRender<PanBar>(ValueProperty, AccentColorProperty, IsEnabledProperty);
    }

    public PanBar()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Height = 20;
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color AccentColor { get => GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

    /// <summary>Độ rõ (alpha) của gradient bên trái/phải ứng với giá trị pan.</summary>
    public static (double Left, double Right) SideAlphas(double pan)
    {
        pan = Math.Clamp(pan, -1, 1);
        double left = IdleAlpha + (ActiveAlpha - IdleAlpha) * Math.Max(0, -pan);
        double right = IdleAlpha + (ActiveAlpha - IdleAlpha) * Math.Max(0, pan);
        return (left, right);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 8 || h <= 4) return;

        double radius = h / 2;
        var full = new Rect(0, 0, w, h);
        double mid = w / 2;

        using var dim = context.PushOpacity(IsEnabled ? 1.0 : 0.45);

        context.DrawRectangle(PillBrush, null, full, radius, radius);

        var (aLeft, aRight) = SideAlphas(Value);
        var c = AccentColor;
        using (context.PushClip(new RoundedRect(full, radius)))
        {
            context.DrawRectangle(Fade(c, aLeft, 0, mid), null, new Rect(0, 0, mid, h));
            context.DrawRectangle(Fade(c, aRight, w, mid), null, new Rect(mid, 0, mid, h));
        }

        // Vạch mờ đánh dấu điểm giữa.
        double cx = Math.Round(mid) + 0.5;
        context.DrawLine(CenterPen, new Point(cx, 4), new Point(cx, h - 4));

        DrawLabel(context, "L", 8, h);
        DrawLabel(context, "R", w - 8 - 6, h);

        context.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);

        double x = Math.Clamp((Value + 1) / 2 * w, 4, w - 4);
        double thumbHeight = h - 6;
        context.DrawRectangle(ThumbBrush, null,
            new Rect(x - 1.5, (h - thumbHeight) / 2, 3, thumbHeight), 1.5, 1.5);
    }

    /// <summary>Gradient từ mép (alpha cho trước) về điểm giữa (trong suốt), tọa độ tuyệt đối.</summary>
    private static LinearGradientBrush Fade(Color c, double alpha, double edgeX, double centerX)
        => new()
        {
            StartPoint = new RelativePoint(edgeX, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(centerX, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B), 0.0),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1.0),
            },
        };

    private static void DrawLabel(DrawingContext context, string text, double x, double h)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 9, LabelBrush);
        context.DrawText(ft, new Point(x, (h - ft.Height) / 2));
    }

    // ---------- kéo bằng chuột ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            Value = 0;
            e.Handled = true;
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        SetFromPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SetFromPointer(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void SetFromPointer(PointerEventArgs e)
    {
        double w = Bounds.Width;
        if (w <= 0) return;
        double v = e.GetPosition(this).X / w * 2 - 1;
        // Hút vào giữa khi gần tâm để dễ về 0.
        Value = Math.Abs(v) < 0.04 ? 0 : v;
    }
}
