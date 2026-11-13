using System.Numerics;

namespace SoundMaster.Core.Analysis;

/// <summary>
/// Ước lượng tempo và độ ổn định tempo từ audio: đường bao onset (spectral flux) → tự tương quan.
/// Tempo toàn bài lấy từ cả đường bao; độ ổn định đo bằng độ lệch của tempo từng cửa sổ 8 s
/// (bước 4 s) so với tempo toàn bài. Trả về null khi nhạc không có nhịp đều đủ rõ để đo.
/// </summary>
public static class TempoAnalyzer
{
    private const int FrameSize = 1024;
    private const int Hop = 512;
    private const double MinBpm = 60, MaxBpm = 200;
    private const double WindowSeconds = 8, StepSeconds = 4;
    private const double MinPeriodicity = 0.15;

    public sealed record Result(double Bpm, double? Stability);

    public static Result? Analyze(float[] left, float[] right, int sampleRate, CancellationToken ct = default)
    {
        int n = left.Length;
        int maxSamples = sampleRate * 240;
        int from = n > maxSamples ? (n - maxSamples) / 2 : 0;
        int to = Math.Min(n, from + maxSamples);
        if (to - from < sampleRate * 6) return null;

        double fps = (double)sampleRate / Hop;
        var env = OnsetEnvelope(left, right, from, to, sampleRate, ct);

        var (bpm, strength) = EstimateTempo(env, 0, env.Length, fps);
        if (bpm is null || strength < MinPeriodicity) return null;
        double global = bpm.Value;

        int win = (int)(WindowSeconds * fps), step = (int)(StepSeconds * fps);
        var devs = new List<double>();
        for (int s = 0; s + win <= env.Length; s += step)
        {
            ct.ThrowIfCancellationRequested();
            var (t, str) = EstimateTempo(env, s, win, fps);
            if (t is null || str < MinPeriodicity) continue;

            // Gấp đôi/nửa tempo (lỗi bát độ) về cùng quãng tám với tempo toàn bài.
            double v = t.Value;
            while (v > global * 1.41) v /= 2;
            while (v < global / 1.41) v *= 2;
            devs.Add(Math.Abs(v - global) / global);
        }

        if (devs.Count < 2) return new Result(global, null);
        // 0% lệch → 100%; lệch trung bình 10% trở lên → 0%.
        double stability = Math.Clamp(1 - devs.Average() * 10, 0, 1);
        return new Result(global, stability);
    }

    private static float[] OnsetEnvelope(float[] l, float[] r, int from, int to, int sampleRate, CancellationToken ct)
    {
        int frames = (to - from - FrameSize) / Hop + 1;
        var env = new float[frames];
        var window = new double[FrameSize];
        for (int i = 0; i < FrameSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));

        const int bins = FrameSize / 2;
        var prev = new double[bins];
        var cur = new double[bins];
        var buf = new Complex[FrameSize];
        for (int f = 0; f < frames; f++)
        {
            if ((f & 255) == 0) ct.ThrowIfCancellationRequested();
            int start = from + f * Hop;
            for (int i = 0; i < FrameSize; i++)
                buf[i] = new Complex(0.5 * (l[start + i] + r[start + i]) * window[i], 0);
            Fft.Transform(buf);

            double flux = 0;
            for (int b = 1; b < bins; b++)
            {
                cur[b] = Math.Log(1 + 100 * buf[b].Magnitude); // nén biên độ
                double d = cur[b] - prev[b];
                if (d > 0) flux += d;
            }
            env[f] = f == 0 ? 0 : (float)flux;
            (prev, cur) = (cur, prev);
        }

        // Trừ trung bình cục bộ (~0.5 s) để chỉ giữ các đỉnh onset.
        int half = (int)(0.25 * sampleRate / Hop);
        return DetrendAndNormalize(env, Math.Max(half, 8));

    }

    private static float[] DetrendAndNormalize(float[] env, int half)
    {
        var cum = new double[env.Length + 1];
        for (int i = 0; i < env.Length; i++) cum[i + 1] = cum[i] + env[i];
        var o = new float[env.Length];
        for (int i = 0; i < env.Length; i++)
        {
            int a = Math.Max(0, i - half), b = Math.Min(env.Length, i + half + 1);
            double mean = (cum[b] - cum[a]) / (b - a);
            o[i] = (float)Math.Max(0, env[i] - mean);
        }
        return o;
    }

    /// <summary>Tempo mạnh nhất trong [start, start+len) và độ "đều nhịp" (tự tương quan chuẩn hóa 0..1).</summary>
    private static (double? Bpm, double Strength) EstimateTempo(float[] env, int start, int len, double fps)
    {
        int minLag = (int)Math.Floor(60 * fps / MaxBpm);
        int maxLag = (int)Math.Ceiling(60 * fps / MinBpm);
        if (len <= maxLag * 2) return (null, 0);

        double energy = 0;
        for (int i = 0; i < len; i++) energy += (double)env[start + i] * env[start + i];
        if (energy < 1e-9) return (null, 0);

        var ac = new double[maxLag + 2];
        for (int lag = minLag - 1; lag <= maxLag + 1; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < len; i++) s += (double)env[start + i] * env[start + i + lag];
            ac[lag] = s / energy * len / (len - lag); // chuẩn hóa theo số mẫu chồng lấn
        }

        // Ưu tiên nhẹ quanh 110 BPM (phân phối log-chuẩn, σ = 1 quãng tám) để tránh nhầm bát độ.
        double bestScore = 0;
        int bestLag = -1;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            if (ac[lag] < ac[lag - 1] || ac[lag] < ac[lag + 1]) continue; // chỉ xét đỉnh
            double bpm = 60 * fps / lag;
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 110), 2));
            double score = ac[lag] * (0.6 + 0.4 * prior);
            if (score > bestScore) { bestScore = score; bestLag = lag; }
        }
        if (bestLag < 0) return (null, 0);

        // Nội suy parabol quanh đỉnh để lấy lag phân số.
        double y0 = ac[bestLag - 1], y1 = ac[bestLag], y2 = ac[bestLag + 1];
        double denom = y0 - 2 * y1 + y2;
        double shift = Math.Abs(denom) < 1e-12 ? 0 : 0.5 * (y0 - y2) / denom;
        double frac = bestLag + Math.Clamp(shift, -1, 1);
        return (60 * fps / frac, Math.Clamp(ac[bestLag], 0, 1));
    }
}
