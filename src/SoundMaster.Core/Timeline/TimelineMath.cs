using System.Globalization;

namespace SoundMaster.Core.Timeline;

/// <summary>Một vạch lưới dọc tại vị trí X (pixel); IsBar = vạch ô nhịp, ngược lại là vạch phách.</summary>
public readonly record struct GridLine(double X, bool IsBar);

/// <summary>Các phép tính thuần cho timeline: định dạng thời gian, bước chia thước, đỉnh waveform.</summary>
public static class TimelineMath
{
    private static readonly double[] TimeSteps = [0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];

    /// <summary>"mm:ss.mmm"</summary>
    public static string Clock(double seconds)
    {
        seconds = Math.Max(0, seconds);
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        int whole = (int)rest;
        int millis = (int)((rest - whole) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{whole:00}.{millis:000}");
    }

    /// <summary>"bar.beat.sixteenth", ví dụ 001.1.1.</summary>
    public static string Bars(double seconds, double bpm, int beatsPerBar = 4)
    {
        if (bpm <= 0 || beatsPerBar <= 0) return "001.1.1";
        double beats = Math.Max(0, seconds) * bpm / 60.0;
        long bar = (long)(beats / beatsPerBar) + 1;
        int beat = (int)(beats % beatsPerBar) + 1;
        int sixteenth = (int)((beats % 1) * 4) + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{bar:000}.{beat}.{sixteenth}");
    }

    /// <summary>Nhãn thước thời gian; có phần thập phân nếu bước nhỏ hơn 1 giây.</summary>
    public static string RulerLabel(double seconds, double step)
    {
        seconds = Math.Max(0, seconds);
        int minutes = (int)(seconds / 60);
        double rest = seconds - minutes * 60;
        int whole = (int)rest;
        if (step >= 1) return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{whole:00}");
        int tenths = (int)Math.Round((rest - whole) * 10);
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{whole:00}.{tenths}");
    }

    /// <summary>Bước thời gian "đẹp" nhỏ nhất sao cho hai nhãn cách nhau ít nhất <paramref name="minPixels"/>.</summary>
    public static double NiceTimeStep(double pixelsPerSecond, double minPixels = 80)
    {
        foreach (double step in TimeSteps)
        {
            if (step * pixelsPerSecond >= minPixels) return step;
        }
        return TimeSteps[^1];
    }

    /// <summary>
    /// Các vạch lưới dọc trong vùng nhìn thấy: vạch ô nhịp (IsBar) và, khi đủ rộng, vạch phách.
    /// Khi zoom xa, chỉ giữ vạch ô nhịp thưa dần để lưới không dày đặc.
    /// </summary>
    public static List<GridLine> GridLines(double offsetSeconds, double pixelsPerSecond, double width,
        double bpm, int beatsPerBar, double minBarPixels = 8, double minBeatPixels = 24)
    {
        var lines = new List<GridLine>();
        if (pixelsPerSecond <= 0 || width <= 0 || bpm <= 0 || beatsPerBar <= 0) return lines;

        double beatSec = 60.0 / bpm;
        double barSec = beatSec * beatsPerBar;
        double barPx = barSec * pixelsPerSecond;
        double beatPx = beatSec * pixelsPerSecond;

        int every = 1;
        while (every * barPx < minBarPixels && every < 1 << 20) every *= 2;

        long first = Math.Max(0, (long)Math.Floor(offsetSeconds / barSec));
        long last = (long)Math.Ceiling((offsetSeconds + width / pixelsPerSecond) / barSec);

        for (long bar = first; bar <= last; bar++)
        {
            double x = (bar * barSec - offsetSeconds) * pixelsPerSecond;
            if (bar % every == 0) lines.Add(new GridLine(x, true));

            if (every == 1 && beatPx >= minBeatPixels)
            {
                for (int beat = 1; beat < beatsPerBar; beat++)
                    lines.Add(new GridLine(x + beat * beatPx, false));
            }
        }

        return lines;
    }

    /// <summary>Đỉnh lớn nhất trong [startBucket, endBucket) (tự cắt về phạm vi hợp lệ).</summary>
    public static float MaxPeak(float[] peaks, int startBucket, int endBucket)
    {
        int start = Math.Max(0, startBucket);
        int end = Math.Min(peaks.Length, endBucket);
        float max = 0f;
        for (int i = start; i < end; i++)
        {
            if (peaks[i] > max) max = peaks[i];
        }
        return max;
    }
}
