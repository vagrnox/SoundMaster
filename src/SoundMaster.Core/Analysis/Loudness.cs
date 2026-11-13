namespace SoundMaster.Core.Analysis;

/// <summary>
/// Độ ồn tích hợp theo ITU-R BS.1770-4: lọc K-weighting (shelf + high-pass), khối 400 ms
/// chồng 75%, cổng tuyệt đối -70 LUFS rồi cổng tương đối -10 LU.
/// </summary>
public static class Loudness
{
    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

    public static double? Integrated(float[] left, float[] right, int sampleRate)
    {
        int n = left.Length;
        int block = (int)(0.4 * sampleRate);
        int hop = block / 4;
        if (n < block) return null;

        var (shelf, hp) = Filters(sampleRate);
        var zl = Filter(left, shelf, hp);
        var zr = Filter(right, shelf, hp);

        // Tổng bình phương tích lũy để tính năng lượng mỗi khối O(1).
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++)
            cum[i + 1] = cum[i] + (double)zl[i] * zl[i] + (double)zr[i] * zr[i];

        var energies = new List<double>();
        for (int start = 0; start + block <= n; start += hop)
        {
            // L và R cùng trọng số 1.0 → tổng năng lượng hai kênh / số mẫu khối.
            energies.Add((cum[start + block] - cum[start]) / block);
        }

        static double ToLufs(double e) => -0.691 + 10 * Math.Log10(e);

        var abs = energies.Where(e => e > 0 && ToLufs(e) > -70).ToList();
        if (abs.Count == 0) return null;
        double relThreshold = ToLufs(abs.Average()) - 10;
        var gated = abs.Where(e => ToLufs(e) > relThreshold).ToList();
        if (gated.Count == 0) return null;
        return ToLufs(gated.Average());
    }

    private static (Biquad shelf, Biquad hp) Filters(int fs)
    {
        // Công thức BS.1770 tổng quát cho mọi tần số lấy mẫu.
        double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
        double K = Math.Tan(Math.PI * f0 / fs);
        double Vh = Math.Pow(10, G / 20), Vb = Math.Pow(Vh, 0.4996667741545416);
        double a0 = 1 + K / Q + K * K;
        var shelf = new Biquad(
            (Vh + Vb * K / Q + K * K) / a0,
            2 * (K * K - Vh) / a0,
            (Vh - Vb * K / Q + K * K) / a0,
            2 * (K * K - 1) / a0,
            (1 - K / Q + K * K) / a0);

        f0 = 38.13547087602444; Q = 0.5003270373238773;
        K = Math.Tan(Math.PI * f0 / fs);
        double d = 1 + K / Q + K * K;
        var hp = new Biquad(1, -2, 1, 2 * (K * K - 1) / d, (1 - K / Q + K * K) / d);
        return (shelf, hp);
    }

    private static float[] Filter(float[] x, Biquad s1, Biquad s2)
    {
        var y = new float[x.Length];
        double a1 = 0, a2 = 0, b1 = 0, b2 = 0; // trạng thái DF-II transposed
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double o = s1.B0 * v + a1;
            a1 = s1.B1 * v - s1.A1 * o + a2;
            a2 = s1.B2 * v - s1.A2 * o;

            double p = s2.B0 * o + b1;
            b1 = s2.B1 * o - s2.A1 * p + b2;
            b2 = s2.B2 * o - s2.A2 * p;
            y[i] = (float)p;
        }
        return y;
    }
}
