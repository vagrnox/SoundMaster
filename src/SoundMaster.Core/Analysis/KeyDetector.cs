using System.Numerics;

namespace SoundMaster.Core.Analysis;

/// <summary>
/// Dò giọng: chroma 12 nốt từ phổ (FFT 8192, 60–2000 Hz) rồi so với profile
/// Krumhansl-Schmuckler cho 24 giọng. Chạy trên tối đa ~3 phút ở giữa bài để nhanh.
/// </summary>
public static class KeyDetector
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly double[] MajorProfile = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
    private static readonly double[] MinorProfile = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

    private const int FftSize = 8192;
    private const int Hop = 4096;

    public static (string? Key, bool IsMajor, double Confidence) Detect(
        float[] left, float[] right, int sampleRate, CancellationToken ct = default)
    {
        int n = left.Length;
        int maxSamples = sampleRate * 180;
        int from = n > maxSamples ? (n - maxSamples) / 2 : 0;
        int to = Math.Min(n, from + maxSamples);
        if (to - from < FftSize) return (null, true, 0);

        var window = new double[FftSize];
        for (int i = 0; i < FftSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));

        // Bin → lớp cao độ (-1 nếu ngoài dải).
        int lo = (int)Math.Ceiling(60.0 * FftSize / sampleRate);
        int hi = Math.Min(FftSize / 2 - 1, (int)(2000.0 * FftSize / sampleRate));
        var binClass = new int[hi + 1];
        for (int b = 0; b <= hi; b++)
        {
            if (b < lo) { binClass[b] = -1; continue; }
            double freq = (double)b * sampleRate / FftSize;
            double midi = 69 + 12 * Math.Log2(freq / 440.0);
            binClass[b] = (((int)Math.Round(midi) % 12) + 12) % 12;
        }

        var chroma = new double[12];
        var buf = new Complex[FftSize];
        for (int start = from; start + FftSize <= to; start += Hop)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < FftSize; i++)
                buf[i] = new Complex(0.5 * (left[start + i] + right[start + i]) * window[i], 0);
            Fft.Transform(buf);
            for (int b = lo; b <= hi; b++)
            {
                // Nén biên độ để hài cao/đỉnh to không lấn át.
                chroma[binClass[b]] += Math.Sqrt(buf[b].Magnitude);
            }
        }

        if (chroma.Sum() <= 1e-9) return (null, true, 0);

        double best = double.NegativeInfinity;
        int bestKey = 0;
        bool bestMajor = true;
        for (int k = 0; k < 12; k++)
        {
            double cMaj = Correlate(chroma, MajorProfile, k);
            double cMin = Correlate(chroma, MinorProfile, k);
            if (cMaj > best) { best = cMaj; bestKey = k; bestMajor = true; }
            if (cMin > best) { best = cMin; bestKey = k; bestMajor = false; }
        }
        return (Names[bestKey], bestMajor, Math.Clamp(best, 0, 1));
    }

    private static double Correlate(double[] chroma, double[] profile, int tonic)
    {
        double mc = chroma.Average(), mp = profile.Average();
        double num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double c = chroma[(i + tonic) % 12] - mc;
            double p = profile[i] - mp;
            num += c * p; dc += c * c; dp += p * p;
        }
        return dc <= 0 ? 0 : num / Math.Sqrt(dc * dp);
    }
}
