using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SoundMaster.App.Controls;

/// <summary>
/// Thanh volume dạng viên thuốc nền đen: vạch xanh là mức volume (kéo qua lại, double-click để về 0 dB),
/// bên trong là đồng hồ mức gradient theo màu <see cref="AccentColor"/>. Đồng hồ dùng cùng thang
/// -60…+6 dB với volume, nên thanh đầy nghĩa là chạm mức volume tối đa.
/// </summary>
public class VolumeBar : Control
{
    public const double MinDb = -60;
    public const double MaxDb = 6;
    public const double DefaultDb = 0;

    public static readonly StyledProperty<double> ValueDbProperty =
        AvaloniaProperty.Register<VolumeBar, double>(nameof(ValueDb), DefaultDb,
            defaultBindingMode: BindingMode.TwoWay,
            coerce: (_, v) => Math.Clamp(v, MinDb, MaxDb));

    public static readonly StyledProperty<double> LeftDbProperty =
        AvaloniaProperty.Register<VolumeBar, double>(nameof(LeftDb), -90);

    public static readonly StyledProperty<double> RightDbProperty =
        AvaloniaProperty.Register<VolumeBar, double>(nameof(RightDb), -90);

    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<VolumeBar, Color>(nameof(AccentColor), Color.Parse("#3DDC84"));

    private static readonly IBrush PillBrush = new ImmutableSolidColorBrush(Color.Parse("#000000"));
    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2E2E2E")), 1);
    private static readonly IPen UnityPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.22), 1);
    private static readonly IBrush ThumbBrush = new ImmutableSolidColorBrush(Color.Parse("#3DDC84"));

    private bool _dragging;

    static VolumeBar()
    {
        AffectsRender<VolumeBar>(ValueDbProperty, LeftDbProperty, RightDbProperty, AccentColorProperty, IsEnabledProperty);
    }

    public VolumeBar()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        Height = 20;
    }

    public double ValueDb { get => GetValue(ValueDbProperty); set => SetValue(ValueDbProperty, value); }
    public double LeftDb { get => GetValue(LeftDbProperty); set => SetValue(LeftDbProperty, value); }
    public double RightDb { get => GetValue(RightDbProperty); set => SetValue(RightDbProperty, value); }
    public Color AccentColor { get => GetValue(AccentColorProperty); set => SetValue(AccentColorProperty, value); }

    /// <summary>Vị trí dB → tỉ lệ 0..1 theo chiều ngang (dùng chung cho vạch volume và đồng hồ mức).</summary>
    public static double DbToFraction(double db)
        => (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);

    public static double FractionToDb(double fraction)
        => MinDb + Math.Clamp(fraction, 0, 1) * (MaxDb - MinDb);

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 8 || h <= 4) return;

        double radius = h / 2;
        var full = new Rect(0, 0, w, h);

        using var dim = context.PushOpacity(IsEnabled ? 1.0 : 0.45);

        context.DrawRectangle(PillBrush, null, full, radius, radius);

        // Đồng hồ mức: gradient cố định theo vị trí (tối ở trái, sáng ở phải), cắt theo hình viên thuốc.
        double level = Math.Max(LeftDb, RightDb);
        double fillWidth = level <= MinDb ? 0 : DbToFraction(level) * w;
        if (fillWidth > 0.5)
        {
            var c = AccentColor;
            var gradient = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(w, 0, RelativeUnit.Absolute),
                GradientStops =
                {
                    new GradientStop(Scale(c, 0.30), 0.0),
                    new GradientStop(Scale(c, 0.70), 0.55),
                    new GradientStop(c, 1.0),
                },
            };
            using (context.PushClip(new RoundedRect(full, radius)))
            {
                context.DrawRectangle(gradient, null, new Rect(0, 0, fillWidth, h));
            }
        }

        // Vạch mờ đánh dấu 0 dB (mức gốc).
        double unityX = Math.Round(DbToFraction(0) * w) + 0.5;
        context.DrawLine(UnityPen, new Point(unityX, 4), new Point(unityX, h - 4));

        context.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);

        // Vạch xanh = mức volume.
        double x = Math.Clamp(DbToFraction(ValueDb) * w, 4, w - 4);
        double thumbHeight = h - 6;
        context.DrawRectangle(ThumbBrush, null,
            new Rect(x - 1.5, (h - thumbHeight) / 2, 3, thumbHeight), 1.5, 1.5);
    }

    private static Color Scale(Color c, double factor)
        => Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    // ---------- kéo bằng chuột ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            ValueDb = DefaultDb;
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
        ValueDb = FractionToDb(e.GetPosition(this).X / w);
    }
}
