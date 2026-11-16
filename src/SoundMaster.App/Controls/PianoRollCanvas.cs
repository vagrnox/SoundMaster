using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.App.ViewModels;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Timeline;
using SoundMaster.Core.Transcription;

namespace SoundMaster.App.Controls;

/// <summary>
/// Lưới piano roll: hàng theo nửa cung, cột theo nhịp, waveform của track làm nền, các nốt đã bóc đặt đúng
/// vị trí / độ dài (vàng = nốt chính, đỏ = nốt nghi ngờ). Chuột trái chọn nốt (và nghe thử), chuột phải xóa nốt.
/// </summary>
public class PianoRollCanvas : Control
{
    public static readonly StyledProperty<TrackViewModel?> TrackProperty =
        AvaloniaProperty.Register<PianoRollCanvas, TrackViewModel?>(nameof(Track));

    public static readonly StyledProperty<double> TopNoteProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(TopNote), 84);

    public static readonly StyledProperty<double> RowHeightProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(RowHeight), 18);

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(PixelsPerSecond), 20);

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<double> PositionSecondsProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(PositionSeconds));

    public static readonly StyledProperty<TranscribedNote?> SelectedNoteProperty =
        AvaloniaProperty.Register<PianoRollCanvas, TranscribedNote?>(nameof(SelectedNote), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> SnapSecondsProperty =
        AvaloniaProperty.Register<PianoRollCanvas, double>(nameof(SnapSeconds));

    private static readonly Color Gold = Color.Parse("#F1AB29");
    private static readonly Color Red = Color.Parse("#E5484D");
    private static readonly IBrush RowWhite = new ImmutableSolidColorBrush(Color.Parse("#1D2025"));
    private static readonly IBrush RowBlack = new ImmutableSolidColorBrush(Color.Parse("#14161A"));
    private static readonly IBrush MainNote = new ImmutableSolidColorBrush(Gold, 0.96);
    private static readonly IBrush MainNotePlaying = new ImmutableSolidColorBrush(Color.Parse("#FFD27A"));
    private static readonly IBrush DoubtNote = new ImmutableSolidColorBrush(Red, 0.88);
    private static readonly IBrush DoubtNotePlaying = new ImmutableSolidColorBrush(Color.Parse("#FF8A8E"));
    private static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.Parse("#16130B"));
    private static readonly IPen RowLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.04), 1);
    private static readonly IPen OctaveLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.16), 1);
    private static readonly IPen BeatPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.07), 1);
    private static readonly IPen BarPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#FFFFFF"), 0.22), 1);
    private static readonly IPen SelectedPen = new ImmutablePen(Brushes.White, 2);

    private TrackViewModel? _vm;

    /// <summary>Nghe thử nốt khi bấm vào nốt (gọi lúc bấm / thả).</summary>
    public event Action<int>? NotePressed;
    public event Action? NoteReleased;
    public event Action<TranscribedNote>? NoteDeleteRequested;
    public event Action<TranscribedNote, TranscribedNote>? NoteChanged;
    public event Action<double, int>? NoteAddRequested;

    static PianoRollCanvas()
    {
        AffectsRender<PianoRollCanvas>(TrackProperty, TopNoteProperty, RowHeightProperty, PixelsPerSecondProperty,
            OffsetSecondsProperty, BpmProperty, PositionSecondsProperty, SelectedNoteProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PianoRollCanvas>(true);
    }

    public TrackViewModel? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double TopNote { get => GetValue(TopNoteProperty); set => SetValue(TopNoteProperty, value); }
    public double RowHeight { get => GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }
    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public double PositionSeconds { get => GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }
    public TranscribedNote? SelectedNote { get => GetValue(SelectedNoteProperty); set => SetValue(SelectedNoteProperty, value); }

    /// <summary>Bước bám lưới khi kéo / vẽ nốt (giây); 0 = không bám.</summary>
    public double SnapSeconds { get => GetValue(SnapSecondsProperty); set => SetValue(SnapSecondsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TrackProperty) return;

        if (_vm is not null) _vm.PropertyChanged -= OnTrackChanged;
        _vm = change.NewValue as TrackViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnTrackChanged;
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackViewModel.Notes) or nameof(TrackViewModel.ColorBrush))
            InvalidateVisual();
    }

    private double XAt(double seconds) => (seconds - OffsetSeconds) * PixelsPerSecond;
    private double YOfTop(int note) => (TopNote - (note + 1)) * RowHeight;

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, rh = RowHeight;
        context.DrawRectangle(RowBlack, null, new Rect(0, 0, w, h));

        // Hàng nửa cung: phím trắng sáng hơn phím đen; vạch đậm ở ranh giới C.
        int hi = Math.Min(127, (int)Math.Ceiling(TopNote)), lo = Math.Max(0, (int)Math.Floor(TopNote - h / rh));
        for (int n = hi; n >= lo; n--)
        {
            double y = YOfTop(n);
            if (!PianoKeysView.IsBlack(n)) context.DrawRectangle(RowWhite, null, new Rect(0, y, w, rh));
            context.DrawLine(n % 12 == 0 ? OctaveLine : RowLine, new Point(0, y + rh), new Point(w, y + rh));
        }

        DrawBeatLines(context, w, h);
        DrawWaveform(context, w, h);
        DrawNotes(context, w, h);
    }

    private void DrawBeatLines(DrawingContext context, double w, double h)
    {
        double beatSec = 60.0 / Math.Max(1, Bpm);
        double beatPx = beatSec * PixelsPerSecond;
        if (beatPx < 4) return;

        long first = (long)Math.Floor(OffsetSeconds / beatSec);
        long last = (long)Math.Ceiling((OffsetSeconds + w / PixelsPerSecond) / beatSec);
        for (long b = first; b <= last; b++)
        {
            bool bar = b % TimelineViewModel.BeatsPerBar == 0;
            if (!bar && beatPx < 14) continue;
            double x = Math.Round(XAt(b * beatSec)) + 0.5;
            context.DrawLine(bar ? BarPen : BeatPen, new Point(x, 0), new Point(x, h));
        }
    }

    /// <summary>Waveform của stem làm nền (mờ), để thấy nốt khớp với âm thanh thật.</summary>
    private void DrawWaveform(DrawingContext context, double w, double h)
    {
        var audio = _vm?.Audio;
        if (audio is null) return;

        const double step = 3, barWidth = 2;
        double pps = PixelsPerSecond;
        double bucketSec = AudioTrack.PeakBucketFrames / (double)EngineFormat.SampleRate;
        double mid = h / 2, maxAmp = h * 0.36;
        var color = (_vm!.ColorBrush as ISolidColorBrush)?.Color ?? Colors.Gray;
        IBrush brush = new ImmutableSolidColorBrush(color, 0.32);

        double worldLeft = OffsetSeconds * pps;
        int firstCol = (int)Math.Floor(worldLeft / step);
        int lastCol = (int)Math.Ceiling((worldLeft + w) / step);
        for (int c = firstCol; c < lastCol; c++)
        {
            double t0 = c * step / pps, t1 = (c + 1) * step / pps;
            int b0 = (int)Math.Floor(t0 / bucketSec);
            int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(t1 / bucketSec));
            double peak = TimelineMath.MaxPeak(audio.Peaks, b0, b1);
            double amp = Math.Max(0.5, peak * maxAmp);
            context.DrawRectangle(brush, null, new Rect(c * step - worldLeft, mid - amp, barWidth, amp * 2), 1, 1);
        }
    }

    private void DrawNotes(DrawingContext context, double w, double h)
    {
        var notes = _vm?.Notes;
        if (notes is null) return;

        double rh = RowHeight;
        foreach (var n in notes)
        {
            var r = NoteRect(n);
            if (r.Right < 0 || r.X > w || r.Bottom < 0 || r.Y > h) continue;

            bool playing = PositionSeconds >= n.StartSeconds && PositionSeconds < n.EndSeconds;
            IBrush fill = n.IsDoubtful ? (playing ? DoubtNotePlaying : DoubtNote) : (playing ? MainNotePlaying : MainNote);
            context.DrawRectangle(fill, null, r, 4, 4);

            if (ReferenceEquals(n, SelectedNote))
                context.DrawRectangle(null, SelectedPen, r.Inflate(1), 3, 3);

            if (r.Width >= 15 && rh >= 12)
            {
                var label = new FormattedText(n.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), Math.Clamp(rh * 0.58, 9, 14),
                    n.IsDoubtful ? Brushes.White : Ink);
                using (context.PushClip(r))
                    context.DrawText(label, new Point(r.X + 4, r.Y + (r.Height - label.Height) / 2));
            }
        }
    }

    private Rect NoteRect(TranscribedNote n)
    {
        double x0 = XAt(n.StartSeconds), x1 = XAt(n.EndSeconds);
        return new Rect(x0, YOfTop(n.MidiNote) + 1, Math.Max(3, x1 - x0), Math.Max(2, RowHeight - 2));
    }

    private TranscribedNote? HitTest(Point p)
    {
        var notes = _vm?.Notes;
        if (notes is null) return null;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            if (NoteRect(notes[i]).Inflate(new Thickness(1.5, 0)).Contains(p)) return notes[i];
        }
        return null;
    }

    private enum DragMode { None, Move, ResizeStart, ResizeEnd }

    private const double EdgeGrab = 6;
    private const double DragThreshold = 3;
    private const double MinLength = 0.03;

    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor EdgeCursor = new(StandardCursorType.SizeWestEast);

    private (ulong Time, Point Pos)? _lastBlankPress;
    private DragMode _mode;
    private TranscribedNote? _cur;       // phiên bản hiện tại của nốt đang kéo
    private Point _pressPoint;
    private bool _moved;
    private double _grabTime;            // con trỏ cách đầu nốt bao nhiêu giây lúc bấm
    private int _grabPitch;              // con trỏ cách cao độ nốt bao nhiêu nửa cung lúc bấm

    private double TimeAt(double x) => OffsetSeconds + x / PixelsPerSecond;
    private int PitchAt(double y) => (int)Math.Floor(TopNote - y / RowHeight);

    private double Snap(double seconds)
        => SnapSeconds > 0 ? Math.Round(seconds / SnapSeconds) * SnapSeconds : seconds;

    private DragMode ModeAt(TranscribedNote n, Point p)
    {
        var r = NoteRect(n);
        if (r.Width > 14 && p.X <= r.X + EdgeGrab) return DragMode.ResizeStart;
        if (r.Width > 14 && p.X >= r.Right - EdgeGrab) return DragMode.ResizeEnd;
        return DragMode.Move;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);
        var hit = HitTest(pos);

        if (props.IsRightButtonPressed)
        {
            if (hit is not null) NoteDeleteRequested?.Invoke(hit);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Đúp-click vào chỗ trống: vẽ nốt mới.
        bool second = hit is null && _lastBlankPress is { } last && e.Timestamp - last.Time <= 450 && Math.Abs(pos.X - last.Pos.X) < 8 && Math.Abs(pos.Y - last.Pos.Y) < 8;
        _lastBlankPress = hit is null && !second ? (e.Timestamp, pos) : null;
        if (second)
        {
            NoteAddRequested?.Invoke(TimeAt(pos.X), PitchAt(pos.Y));
            e.Handled = true;
            return;
        }

        SelectedNote = hit;
        if (hit is not null)
        {
            _cur = hit;
            _mode = ModeAt(hit, pos);
            _pressPoint = pos;
            _moved = false;
            _grabTime = TimeAt(pos.X) - hit.StartSeconds;
            _grabPitch = PitchAt(pos.Y) - hit.MidiNote;
            e.Pointer.Capture(this);
            NotePressed?.Invoke(hit.MidiNote);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_cur is null || _mode == DragMode.None)
        {
            var over = HitTest(pos);
            Cursor = over is null ? null : ModeAt(over, pos) == DragMode.Move ? MoveCursor : EdgeCursor;
            return;
        }

        if (!_moved && Math.Abs(pos.X - _pressPoint.X) < DragThreshold && Math.Abs(pos.Y - _pressPoint.Y) < DragThreshold) return;
        _moved = true;

        var note = _cur;
        TranscribedNote updated = note;
        switch (_mode)
        {
            case DragMode.Move:
            {
                double start = Math.Max(0, Snap(TimeAt(pos.X) - _grabTime));
                int midi = Math.Clamp(PitchAt(pos.Y) - _grabPitch, 0, 127);
                updated = note with { StartSeconds = start, EndSeconds = start + note.Duration, MidiNote = midi };
                break;
            }
            case DragMode.ResizeStart:
            {
                double min = SnapSeconds > 0 ? SnapSeconds : MinLength;
                double start = Math.Clamp(Snap(TimeAt(pos.X)), 0, note.EndSeconds - Math.Min(min, note.Duration));
                updated = note with { StartSeconds = start };
                break;
            }
            case DragMode.ResizeEnd:
            {
                double min = SnapSeconds > 0 ? SnapSeconds : MinLength;
                double end = Math.Max(note.StartSeconds + min, Snap(TimeAt(pos.X)));
                updated = note with { EndSeconds = end };
                break;
            }
        }

        if (updated == note) return;

        bool pitchChanged = updated.MidiNote != note.MidiNote;
        _cur = updated;
        NoteChanged?.Invoke(note, updated);
        SelectedNote = updated;
        if (pitchChanged) NotePressed?.Invoke(updated.MidiNote); // nghe nốt mới khi kéo qua cao độ khác
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndHold();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndHold();
    }

    private void EndHold()
    {
        if (_cur is null) return;
        _cur = null;
        _mode = DragMode.None;
        NoteReleased?.Invoke();
    }
}
