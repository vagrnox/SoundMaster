using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.Core.Transcription;

namespace SoundMaster.App.Controls;

/// <summary>Cột phím đàn piano bên trái piano roll: bấm (giữ) để nghe nốt, kéo qua các phím để lướt.</summary>
public class PianoKeysView : Control
{
    public static readonly StyledProperty<double> TopNoteProperty =
        AvaloniaProperty.Register<PianoKeysView, double>(nameof(TopNote), 84);

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<PianoKeysView, double>(nameof(RowHeight), 18);

    private static readonly IBrush WhiteKey = new ImmutableSolidColorBrush(Color.Parse("#D9DCE0"));
    private static readonly IBrush BlackKey = new ImmutableSolidColorBrush(Color.Parse("#17191C"));
    private static readonly IBrush PressedKey = new ImmutableSolidColorBrush(Color.Parse("#F1AB29"));
    private static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.Parse("#2A2D31"));
    private static readonly IPen Divider = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7A7F87")), 1);

    private int _pressed = -1;

    /// <summary>Nốt đang được bấm (-1 nếu không).</summary>
    public int PressedNote => _pressed;

    public event Action<int>? NotePressed;
    public event Action? NoteReleased;

    static PianoKeysView()
    {
        AffectsRender<PianoKeysView>(TopNoteProperty, RowHeightProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PianoKeysView>(true);
    }

    public double TopNote { get => GetValue(TopNoteProperty); set => SetValue(TopNoteProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }

    public static bool IsBlack(int midi)
    {
        int pc = ((midi % 12) + 12) % 12;
        return pc is 1 or 3 or 6 or 8 or 10;
    }

    private int NoteAt(double y) => (int)Math.Floor(TopNote - y / RowHeight);

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, rh = RowHeight;
        context.DrawRectangle(BlackKey, null, new Rect(0, 0, w, h));

        int hi = (int)Math.Ceiling(TopNote), lo = (int)Math.Floor(TopNote - h / rh);
        for (int n = Math.Min(127, hi); n >= Math.Max(0, lo); n--)
        {
            double y = (TopNote - (n + 1)) * rh;
            bool black = IsBlack(n);
            bool pressed = n == _pressed;

            // Phím trắng chiếm cả hàng; phím đen là thanh tối ngắn hơn (kiểu bàn phím thật).
            var fill = pressed ? PressedKey : black ? BlackKey : WhiteKey;
            var rect = new Rect(0, y, black && !pressed ? w * 0.62 : w, rh);
            if (!black || pressed) context.DrawRectangle(fill, null, rect);
            else
            {
                context.DrawRectangle(WhiteKey, null, new Rect(w * 0.62, y, w * 0.38, rh));
                context.DrawRectangle(BlackKey, null, rect);
            }

            if (n % 12 == 0 || pressed)
            {
                if (rh >= 9)
                {
                    var label = new FormattedText(TranscribedNote.NameOf(n), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                        Math.Min(11, rh - 3), pressed || !black ? Ink : Brushes.White);
                    context.DrawText(label, new Point(w - label.Width - 4, y + (rh - label.Height) / 2));
                }
            }

            if (n % 12 == 0 || n % 12 == 5) // ranh giới giữa các phím trắng (C|B và F|E)
                context.DrawLine(Divider, new Point(0, y + rh), new Point(w, y + rh));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(this);
        Press(NoteAt(e.GetPosition(this).Y));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressed < 0) return;
        int n = NoteAt(e.GetPosition(this).Y);
        if (n != _pressed) Press(n);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Release();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Release();
    }

    private void Press(int note)
    {
        if (note < 0 || note > 127) return;
        _pressed = note;
        NotePressed?.Invoke(note);
        InvalidateVisual();
    }

    private void Release()
    {
        if (_pressed < 0) return;
        _pressed = -1;
        NoteReleased?.Invoke();
        InvalidateVisual();
    }
}
