using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SoundMaster.App.Controls;

/// <summary>
/// Thanh tốc độ phát dạng viên thuốc nền đen cùng kiểu <see cref="VolumeBar"/>: giữa là 1x,
/// kéo sang trái giảm dần về <see cref="MinSpeed"/>, sang phải tăng dần đến <see cref="MaxSpeed"/>.
/// Vùng từ tâm tới vạch xanh được tô gradient màu primary. Double-click để về 1x.
/// </summary>
public class SpeedBar : Control
{
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 3.0;
    public const double Step = 0.1;

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SpeedBar, double>(nameof(Value), 1.0,
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(v, MinSpeed, MaxSpeed));

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<SpeedBar, Color>(nameof(AccentColor), Color.Parse("#F1AB29"));

    private static readonly IBrush PillBrush = new ImmutableSolidColorBrush(Color.Parse("#000000"));
    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2E2E2E")), 1);
    private static readonly IPen CenterPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.30), 1);
    private static readonly IBrush ThumbBrush = new ImmutableSolidColorBrush(Color.Parse("#3DDC84"));

    private bool _dragging;

    static SpeedBar()
    {
        AffectsRender<SpeedBar>(ValueProperty, AccentColorProperty, IsEnabledProperty);
    }

    public SpeedBar()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Height = 20;
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Color AccentColor { get => GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

    /// <summary>Tốc độ → vị trí 0..1 (1x ở chính giữa; nửa trái 0.1→1, nửa phải 1→3, mỗi nửa tuyến tính).</summary>
    public static double SpeedToFraction(double speed)
    {
        speed = Math.Clamp(speed, MinSpeed, MaxSpeed);
        return speed <= 1
            ? 0.5 * (speed - MinSpeed) / (1 - MinSpeed)
            : 0.5 + 0.5 * (speed - 1) / (MaxSpeed - 1);
    }

    /// <summary>Vị trí 0..1 → tốc độ, làm tròn theo <see cref="Step"/> (nên giá trị hiển thị trùng giá trị thật).</summary>
    public static double FractionToSpeed(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        double speed = fraction <= 0.5
            ? MinSpeed + (1 - MinSpeed) * (fraction / 0.5)
            : 1 + (MaxSpeed - 1) * ((fraction - 0.5) / 0.5);
        return Math.Clamp(Math.Round(speed / Step) * Step, MinSpeed, MaxSpeed);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 8 || h <= 4) return;

        double radius = h / 2;
        var full = new Rect(0, 0, w, h);
        double mid = w / 2;
        double x = Math.Clamp(SpeedToFraction(Value) * w, 4, w - 4);

        using var dim = context.PushOpacity(IsEnabled ? 1.0 : 0.45);

        context.DrawRectangle(PillBrush, null, full, radius, radius);

        // Gradient từ tâm (mờ) tới vạch xanh (rõ), theo hướng đã kéo.
        if (Math.Abs(x - mid) > 0.5)
        {
            var c = AccentColor;
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(mid, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(x, 0, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(40, c.R, c.G, c.B), 0.0),
                    new GradientStop(Color.FromArgb(235, c.R, c.G, c.B), 1.0),
                },
            };
            var rect = x > mid ? new Rect(mid, 0, x - mid, h) : new Rect(x, 0, mid - x, h);
            using (context.PushClip(new RoundedRect(full, radius)))
            {
                context.DrawRectangle(gradient, null, rect);
            }
        }

        double cx = Math.Round(mid) + 0.5;
        context.DrawLine(CenterPen, new Point(cx, 4), new Point(cx, h - 4));

        context.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);

        double thumbHeight = h - 6;
        context.DrawRectangle(ThumbBrush, null,
            new Rect(x - 1.5, (h - thumbHeight) / 2, 3, thumbHeight), 1.5, 1.5);
    }

    // ---------- kéo bằng chuột ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            Value = 1.0;
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
        double f = e.GetPosition(this).X / w;
        // Hút vào 1x khi gần tâm để dễ về tốc độ gốc.
        Value = Math.Abs(f - 0.5) < 0.025 ? 1.0 : FractionToSpeed(f);
    }
}
