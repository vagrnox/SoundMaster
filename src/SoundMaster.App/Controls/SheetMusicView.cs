using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.Core.Notation;

namespace SoundMaster.App.Controls;

/// <summary>
/// Vẽ bản nhạc (khuông lớn, 4/4) trên nền giấy: khóa, vạch nhịp, nốt / hợp âm / dấu lặng, dây legato,
/// và dòng lời hát dưới mỗi hàng khuông. Nốt đang phát được tô vàng.
/// </summary>
public class SheetMusicView : Control
{
    public static readonly StyledProperty<Score?> ScoreProperty =
        AvaloniaProperty.Register<SheetMusicView, Score?>(nameof(Score));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SheetMusicView, string?>(nameof(Title));

    public static readonly StyledProperty<double> PositionSecondsProperty =
        AvaloniaProperty.Register<SheetMusicView, double>(nameof(PositionSeconds));

    // Kích thước theo "khoảng khuông" (khoảng cách hai dòng kẻ liền nhau).
    private const double Space = 9;
    private const double ClefWidth = 58;
    private const double SideMargin = 28;
    private const double HeaderHeight = 78;
    private const double MinMeasureWidth = 230;
    private const int MaxMeasuresPerSystem = 4;
    private const int TrebleBottomDiatonic = 4 * 7 + 2;  // E4: dòng kẻ dưới cùng khóa Sol
    private const int BassBottomDiatonic = 2 * 7 + 4;    // G2: dòng kẻ dưới cùng khóa Fa

    private static readonly IBrush Paper = new ImmutableSolidColorBrush(Color.Parse("#FBF9F3"));
    private static readonly ImmutableSolidColorBrush Ink = new ImmutableSolidColorBrush(Color.Parse("#1A1A1A"));
    private static readonly ImmutableSolidColorBrush Playing = new ImmutableSolidColorBrush(Color.Parse("#E39200"));
    private static readonly IBrush LyricInk = new ImmutableSolidColorBrush(Color.Parse("#2B3A55"));
    private static readonly IBrush Faint = new ImmutableSolidColorBrush(Color.Parse("#7A7A7A"));
    private static readonly IPen StaffPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 1);
    private static readonly IPen BarPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 1.2);
    private static readonly IPen ThickBarPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 3.5);
    private static readonly IPen StemPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 1.3);
    private static readonly IPen PlayingStemPen = new ImmutablePen(Playing, 1.3);
    private static readonly IPen LedgerPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 1.2);
    private static readonly IPen TiePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1A1A1A")), 1.2);
    private static readonly Typeface Serif = new("Georgia, Times New Roman, serif");
    private static readonly Typeface Symbols = new("Segoe UI Symbol, Noto Music, Bravura, Arial Unicode MS");

    private sealed record Layout(int PerSystem, double MeasureWidth, double SystemHeight, int Systems);

    private Layout _layout = new(1, MinMeasureWidth, 200, 1);

    static SheetMusicView()
    {
        AffectsRender<SheetMusicView>(ScoreProperty, TitleProperty, PositionSecondsProperty);
        AffectsMeasure<SheetMusicView>(ScoreProperty);
    }

    public Score? Score { get => GetValue(ScoreProperty); set => SetValue(ScoreProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double PositionSeconds { get => GetValue(PositionSecondsProperty); set => SetValue(PositionSecondsProperty, value); }

    private static Layout ComputeLayout(Score? score, double width)
    {
        double usable = Math.Max(MinMeasureWidth, width - 2 * SideMargin - ClefWidth);
        int perSystem = Math.Clamp((int)(usable / MinMeasureWidth), 1, MaxMeasuresPerSystem);
        double measureWidth = usable / perSystem;
        int systems = score is null ? 1 : Math.Max(1, (score.Measures + perSystem - 1) / perSystem);
        // Trên 4 khoảng (nốt cao) + khuông Sol 4 + khoảng giữa 6 + khuông Fa 4 + dưới 4 + hàng lời 3.
        double systemHeight = (4 + 4 + 6 + 4 + 4 + 3.2) * Space;
        return new Layout(perSystem, measureWidth, systemHeight, systems);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 900 : availableSize.Width;
        _layout = ComputeLayout(Score, width);
        return new Size(width, HeaderHeight + _layout.Systems * _layout.SystemHeight + 30);
    }

    /// <summary>Kích thước (px) cần có để vẽ ra ảnh với chiều rộng cho trước.</summary>
    public Size DesiredSizeFor(double width)
    {
        var l = ComputeLayout(Score, width);
        return new Size(width, HeaderHeight + l.Systems * l.SystemHeight + 30);
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width;
        context.DrawRectangle(Paper, null, new Rect(0, 0, w, Bounds.Height));
        var layout = ComputeLayout(Score, w);
        _layout = layout;

        DrawHeader(context, w);
        var score = Score;
        if (score is null || (!score.All.Any(e => !e.IsRest) && score.Measures <= 1))
        {
            DrawText(context, "Chưa có nốt nào. Bóc nốt hoặc mở file MIDI trước.", new Point(SideMargin, HeaderHeight + 30), 14, Faint);
            return;
        }

        long playing = (long)Math.Floor(PositionSeconds * score.Bpm / 60.0 * 4);

        for (int sys = 0; sys < layout.Systems; sys++)
        {
            int firstMeasure = sys * layout.PerSystem;
            int count = Math.Min(layout.PerSystem, score.Measures - firstMeasure);
            double top = HeaderHeight + sys * layout.SystemHeight;
            DrawSystem(context, score, layout, sys, firstMeasure, count, top, w, playing);
        }
    }

    private void DrawHeader(DrawingContext context, double w)
    {
        string title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title!;
        var t = Text(title, 24, Ink, FontWeight.SemiBold, Serif);
        context.DrawText(t, new Point((w - t.Width) / 2, 12));
        if (Score is { } s)
            DrawText(context, $"♩ = {Math.Round(s.Bpm)}      4/4", new Point(SideMargin, HeaderHeight - 26), 12, Ink);
    }

    private void DrawSystem(DrawingContext context, Score score, Layout layout, int sys, int firstMeasure, int count,
        double top, double width, long playing)
    {
        double trebleTop = top + 4 * Space;
        double bassTop = trebleTop + 4 * Space + 6 * Space;
        double left = SideMargin;
        double notesLeft = left + ClefWidth;
        double right = notesLeft + layout.PerSystem * layout.MeasureWidth;
        double end = notesLeft + count * layout.MeasureWidth;

        DrawStaffLines(context, trebleTop, left, end);
        DrawStaffLines(context, bassTop, left, end);

        // Đường nối hai khuông ở đầu hàng.
        context.DrawLine(BarPen, new Point(left, trebleTop), new Point(left, bassTop + 4 * Space));

        DrawClef(context, "𝄞", "G", left + 6, trebleTop, treble: true);
        DrawClef(context, "𝄢", "F", left + 6, bassTop, treble: false);

        for (int i = 0; i < count; i++)
        {
            int m = firstMeasure + i;
            double mx = notesLeft + i * layout.MeasureWidth;
            bool last = m == score.Measures - 1;
            double barX = mx + layout.MeasureWidth;

            if (last)
            {
                context.DrawLine(BarPen, new Point(barX - 5, trebleTop), new Point(barX - 5, bassTop + 4 * Space));
                context.DrawLine(ThickBarPen, new Point(barX - 1.5, trebleTop), new Point(barX - 1.5, bassTop + 4 * Space));
            }
            else
            {
                context.DrawLine(BarPen, new Point(barX, trebleTop), new Point(barX, trebleTop + 4 * Space));
                context.DrawLine(BarPen, new Point(barX, bassTop), new Point(barX, bassTop + 4 * Space));
                context.DrawLine(BarPen, new Point(barX, trebleTop + 4 * Space), new Point(barX, bassTop)); // nối hai khuông
            }

            if (i == 0) DrawText(context, (m + 1).ToString(CultureInfo.InvariantCulture), new Point(mx + 2, trebleTop - 2.6 * Space), 10, Faint);

            var accidentals = new Dictionary<(int Staff, int Diatonic), int>();
            foreach (int staff in new[] { 0, 1 })
            {
                double staffTop = staff == 0 ? trebleTop : bassTop;
                foreach (var ev in score.Staff(staff).Where(e => e.Measure == m))
                {
                    double x = mx + 16 + ev.Start16 / 16.0 * (layout.MeasureWidth - 28);
                    bool isPlaying = playing >= ev.Absolute16 && playing < ev.Absolute16 + ev.Duration16;
                    if (ev.IsRest) DrawRest(context, ev, x, staffTop);
                    else DrawChord(context, ev, x, staffTop, staff, isPlaying, accidentals);
                }
            }
        }

        // Dây legato (chỉ vẽ khi nốt nối nằm cùng hàng; sang hàng khác thì bỏ qua).
        foreach (int staff in new[] { 0, 1 })
        {
            var events = score.Staff(staff);
            double staffTop = staff == 0 ? trebleTop : bassTop;
            for (int i = 0; i + 1 < events.Count; i++)
            {
                var a = events[i];
                if (!a.TieToNext || a.Measure < firstMeasure || a.Measure >= firstMeasure + count) continue;
                var b = events[i + 1];
                if (b.Measure >= firstMeasure + count) continue;
                DrawTie(context, a, b, notesLeft, layout, firstMeasure, staffTop, staff);
            }
        }

        // Lời hát dưới mỗi hàng.
        double lyricY = bassTop + 4 * Space + 4.2 * Space;
        foreach (var ev in score.All.Where(e => e.Lyric is not null && e.Measure >= firstMeasure && e.Measure < firstMeasure + count))
        {
            int i = ev.Measure - firstMeasure;
            double x = notesLeft + i * layout.MeasureWidth + 16 + ev.Start16 / 16.0 * (layout.MeasureWidth - 28);
            var t = Text(ev.Lyric!.Display, 13, LyricInk, FontWeight.Normal, Serif);
            context.DrawText(t, new Point(x - t.Width / 2 + Space * 0.6, lyricY));
        }
    }

    private static void DrawStaffLines(DrawingContext context, double top, double x0, double x1)
    {
        for (int i = 0; i < 5; i++)
            context.DrawLine(StaffPen, new Point(x0, top + i * Space), new Point(x1, top + i * Space));
    }

    private void DrawClef(DrawingContext context, string glyph, string fallback, double x, double staffTop, bool treble)
    {
        // Ký hiệu khóa từ font biểu tượng; chữ G / F đặt cạnh để vẫn đọc được nếu máy thiếu font nhạc.
        var t = Text(glyph, treble ? 6.2 * Space : 4.6 * Space, Ink, FontWeight.Normal, Symbols);
        context.DrawText(t, new Point(x, staffTop + (treble ? -1.6 * Space : -0.5 * Space)));
    }

    // ---------- nốt ----------

    private static int StaffPosition(int midi, int staff)
        => ScoreBuilder.DiatonicNumber(midi) - (staff == 0 ? TrebleBottomDiatonic : BassBottomDiatonic);

    private static double YOf(int position, double staffTop) => staffTop + 4 * Space - position * Space / 2.0;

    private void DrawChord(DrawingContext context, ScoreEvent ev, double x, double staffTop, int staff, bool isPlaying,
        Dictionary<(int Staff, int Diatonic), int> accidentals)
    {
        ImmutableSolidColorBrush brush = isPlaying ? Playing : Ink;
        var positions = ev.Midis.Select(m => StaffPosition(m, staff)).ToList();
        int baseValue = BaseValue(ev.Duration16);
        bool filled = baseValue <= 4;
        double avg = positions.Average();
        bool stemUp = avg < 4;

        // Dòng kẻ phụ.
        int low = positions.Min(), high = positions.Max();
        for (int p = -2; p >= low; p -= 2)
            context.DrawLine(LedgerPen, new Point(x - Space * 0.95, YOf(p, staffTop)), new Point(x + Space * 0.95, YOf(p, staffTop)));
        for (int p = 10; p <= high; p += 2)
            context.DrawLine(LedgerPen, new Point(x - Space * 0.95, YOf(p, staffTop)), new Point(x + Space * 0.95, YOf(p, staffTop)));

        // Đầu nốt (các nốt cạnh nhau một bậc đặt lệch sang bên để không chồng nhau).
        bool prevShifted = false;
        int prevPos = int.MinValue;
        var heads = new List<(double X, double Y)>();
        for (int i = 0; i < ev.Midis.Length; i++)
        {
            int pos = positions[i];
            bool shift = prevPos != int.MinValue && pos - prevPos == 1 && !prevShifted;
            prevShifted = shift;
            double hx = shift ? x + Space * 1.05 : x;
            double hy = YOf(pos, staffTop);
            heads.Add((hx, hy));
            prevPos = pos;

            var rect = new Rect(hx - Space * 0.62, hy - Space * 0.46, Space * 1.24, Space * 0.92);
            using (context.PushTransform(Matrix.CreateRotation(-0.35, new Point(hx, hy))))
            {
                if (filled) context.DrawEllipse(brush, null, new Point(hx, hy), rect.Width / 2, rect.Height / 2);
                else context.DrawEllipse(Paper, new ImmutablePen(brush, 1.6), new Point(hx, hy), rect.Width / 2 - 0.6, rect.Height / 2 - 0.4);
            }

            // Dấu thăng (nhắc lại theo từng ô nhịp).
            var (step, alter) = ScoreBuilder.Spell(ev.Midis[i]);
            int dia = ScoreBuilder.DiatonicNumber(ev.Midis[i]);
            accidentals.TryGetValue((staff, dia), out int current);
            if (alter != current)
            {
                var sharp = Text(alter == 1 ? "♯" : "♮", Space * 1.6, brush, FontWeight.Normal, Symbols);
                context.DrawText(sharp, new Point(hx - Space * 1.9, hy - sharp.Height / 2));
                accidentals[(staff, dia)] = alter;
            }

            // Chấm dôi.
            if (ev.Duration16 is 12 or 6 or 3)
            {
                double dy = pos % 2 == 0 ? hy - Space * 0.5 : hy;
                context.DrawEllipse(brush, null, new Point(hx + Space * 1.15, dy), 1.6, 1.6);
            }
        }

        // Cổ nốt + đuôi.
        if (baseValue < 16)
        {
            double xs = stemUp ? x + Space * 0.58 : x - Space * 0.58;
            double yLow = YOf(low, staffTop), yHigh = YOf(high, staffTop);
            double yStart = stemUp ? yLow : yHigh;
            double yEnd = stemUp ? yHigh - Space * 3.3 : yLow + Space * 3.3;
            // Cổ nốt dài tới giữa khuông nếu nốt ở xa.
            if (stemUp && yEnd > YOf(8, staffTop)) yEnd = YOf(8, staffTop);
            if (!stemUp && yEnd < YOf(0, staffTop)) yEnd = YOf(0, staffTop);
            context.DrawLine(isPlaying ? PlayingStemPen : StemPen, new Point(xs, yStart), new Point(xs, yEnd));

            int flags = baseValue switch { 2 => 1, 1 => 2, _ => 0 };
            for (int f = 0; f < flags; f++)
            {
                double fy = stemUp ? yEnd + f * Space * 0.8 : yEnd - f * Space * 0.8;
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(xs, fy), false);
                    double dir = stemUp ? 1 : -1;
                    g.CubicBezierTo(new Point(xs + Space * 0.2, fy + dir * Space * 0.9), new Point(xs + Space * 1.4, fy + dir * Space * 1.1),
                        new Point(xs + Space * 0.9, fy + dir * Space * 2.4));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new ImmutablePen(brush, 1.6), geo);
            }
        }
    }

    private void DrawTie(DrawingContext context, ScoreEvent a, ScoreEvent b, double notesLeft, Layout layout, int firstMeasure,
        double staffTop, int staff)
    {
        double xa = notesLeft + (a.Measure - firstMeasure) * layout.MeasureWidth + 16 + a.Start16 / 16.0 * (layout.MeasureWidth - 28);
        double xb = notesLeft + (b.Measure - firstMeasure) * layout.MeasureWidth + 16 + b.Start16 / 16.0 * (layout.MeasureWidth - 28);
        int pos = StaffPosition(a.Midis[0], staff);
        double y = YOf(pos, staffTop) + (pos >= 4 ? -Space * 0.7 : Space * 0.7);
        double lift = pos >= 4 ? -Space * 1.1 : Space * 1.1;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(xa + Space * 0.4, y), false);
            g.QuadraticBezierTo(new Point((xa + xb) / 2, y + lift), new Point(xb - Space * 0.4, y));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, TiePen, geo);
    }

    private void DrawRest(DrawingContext context, ScoreEvent ev, double x, double staffTop)
    {
        double mid = staffTop + 2 * Space;
        int baseValue = BaseValue(ev.Duration16);
        if (ev.Duration16 == 16)
        {
            // Dấu lặng tròn: khối treo dưới dòng thứ 4.
            context.DrawRectangle(Ink, null, new Rect(x - Space * 0.6, staffTop + Space, Space * 1.2, Space * 0.5));
            return;
        }
        switch (baseValue)
        {
            case 8: // lặng trắng: khối đặt trên dòng giữa
                context.DrawRectangle(Ink, null, new Rect(x - Space * 0.6, mid - Space * 0.5, Space * 1.2, Space * 0.5));
                break;
            case 4: // lặng đen: đường zigzag
            {
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(x - Space * 0.3, mid - Space * 1.3), false);
                    g.LineTo(new Point(x + Space * 0.35, mid - Space * 0.55));
                    g.LineTo(new Point(x - Space * 0.3, mid + Space * 0.1));
                    g.LineTo(new Point(x + Space * 0.35, mid + Space * 0.85));
                    g.LineTo(new Point(x - Space * 0.1, mid + Space * 1.3));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new ImmutablePen(Ink, 2.2), geo);
                break;
            }
            default: // lặng móc: chấm + gạch chéo
            {
                int n = baseValue == 2 ? 1 : 2;
                for (int i = 0; i < n; i++)
                {
                    double y = mid - Space * 0.6 + i * Space * 0.9;
                    context.DrawEllipse(Ink, null, new Point(x - Space * 0.25, y), 2.2, 2.2);
                    context.DrawLine(new ImmutablePen(Ink, 1.6), new Point(x - Space * 0.25, y), new Point(x + Space * 0.45, y - Space * 0.8));
                }
                break;
            }
        }

        if (ev.Duration16 is 12 or 6 or 3)
            context.DrawEllipse(Ink, null, new Point(x + Space * 0.95, mid - Space * 0.25), 1.6, 1.6);
    }

    /// <summary>Giá trị nốt gốc (bỏ chấm dôi): 16 tròn, 8 trắng, 4 đen, 2 móc đơn, 1 móc kép.</summary>
    private static int BaseValue(int duration16) => duration16 switch { 12 => 8, 6 => 4, 3 => 2, _ => duration16 };

    private static FormattedText Text(string s, double size, IBrush brush, FontWeight weight, Typeface face)
        => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(face.FontFamily, FontStyle.Normal, weight), size, brush);

    private static void DrawText(DrawingContext context, string s, Point p, double size, IBrush brush)
        => context.DrawText(Text(s, size, brush, FontWeight.Normal, Serif), p);
}
