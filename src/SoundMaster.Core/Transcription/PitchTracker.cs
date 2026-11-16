namespace SoundMaster.Core.Transcription;

/// <summary>Kết quả dò cao độ cho một khung (frame) âm thanh.</summary>
/// <param name="Time">Thời điểm giữa khung (giây).</param>
/// <param name="FrequencyHz">Tần số cơ bản (Hz); 0 nếu khung không có cao độ rõ (im lặng, ồn, bộ gõ).</param>
/// <param name="Confidence">0..1 (1 − CMNDF tại điểm chọn).</param>
/// <param name="RmsDb">Mức RMS của khung (dBFS).</param>
public readonly record struct PitchFrame(double Time, double FrequencyHz, double Confidence, double RmsDb)
{
    public bool IsVoiced => FrequencyHz > 0;
}

/// <summary>
/// Dò cao độ đơn âm bằng YIN (de Cheveigné &amp; Kawahara 2002): hàm sai khác, chuẩn hóa theo trung bình
/// tích lũy, ngưỡng tuyệt đối, nội suy parabol. Giảm tần số lấy mẫu về ~11 kHz cho nhanh (đủ cho cao độ &lt; 1 kHz).
/// </summary>
public static class PitchTracker
{
    private const int TargetRate = 11025;
    private const int Window = 512;        // ~46 ms ở 11 kHz, dài hơn chu kỳ của 55 Hz (200 mẫu)
    private const int Hop = 128;           // ~11.6 ms
    private const double Threshold = 0.15; // ngưỡng tuyệt đối của YIN
    private const double MaxAcceptable = 0.28;
    private const double SilenceDb = -50;

    public static double HopSeconds(int sampleRate) => (double)Hop * Math.Max(1, sampleRate / TargetRate) / sampleRate;

    public static IReadOnlyList<PitchFrame> Track(
        float[] mono, int sampleRate, double minHz = 55, double maxHz = 1100, CancellationToken ct = default)
    {
        int factor = Math.Max(1, sampleRate / TargetRate);
        double fs = (double)sampleRate / factor;
        var x = Decimate(mono, factor);

        int tauMin = Math.Max(2, (int)(fs / maxHz));
        int tauMax = Math.Min(Window - 1, (int)(fs / minHz) + 1);
        var frames = new List<PitchFrame>();
        if (x.Length < Window + tauMax) return frames;

        var d = new double[tauMax + 2];
        var cmndf = new double[tauMax + 2];

        for (int start = 0; start + Window + tauMax < x.Length; start += Hop)
        {
            if ((start / Hop & 63) == 0) ct.ThrowIfCancellationRequested();
            double time = (start + Window / 2.0) / fs;

            double energy = 0;
            for (int j = 0; j < Window; j++) energy += (double)x[start + j] * x[start + j];
            double rmsDb = energy <= 0 ? -120 : 10 * Math.Log10(energy / Window);
            if (rmsDb < SilenceDb)
            {
                frames.Add(new PitchFrame(time, 0, 0, rmsDb));
                continue;
            }

            // Hàm sai khác d(τ).
            for (int tau = 1; tau <= tauMax + 1; tau++)
            {
                double sum = 0;
                for (int j = 0; j < Window; j++)
                {
                    double diff = x[start + j] - x[start + j + tau];
                    sum += diff * diff;
                }
                d[tau] = sum;
            }

            // Chuẩn hóa theo trung bình tích lũy.
            cmndf[0] = 1;
            double running = 0;
            for (int tau = 1; tau <= tauMax + 1; tau++)
            {
                running += d[tau];
                cmndf[tau] = running <= 0 ? 1 : d[tau] * tau / running;
            }

            // Điểm đầu tiên dưới ngưỡng rồi đi tới cực tiểu địa phương; không có thì lấy cực tiểu toàn cục.
            int best = -1;
            for (int tau = tauMin; tau <= tauMax; tau++)
            {
                if (cmndf[tau] >= Threshold) continue;
                while (tau + 1 <= tauMax && cmndf[tau + 1] < cmndf[tau]) tau++;
                best = tau;
                break;
            }

            if (best < 0)
            {
                double min = double.MaxValue;
                for (int tau = tauMin; tau <= tauMax; tau++)
                {
                    if (cmndf[tau] < min)
                    {
                        min = cmndf[tau];
                        best = tau;
                    }
                }
                if (best < 0 || min > MaxAcceptable)
                {
                    frames.Add(new PitchFrame(time, 0, 0, rmsDb));
                    continue;
                }
            }

            // Nội suy parabol quanh cực tiểu để có chu kỳ phân số.
            double period = best;
            if (best > tauMin && best < tauMax)
            {
                double a = cmndf[best - 1], b = cmndf[best], c = cmndf[best + 1];
                double denom = a - 2 * b + c;
                if (Math.Abs(denom) > 1e-12) period = best + 0.5 * (a - c) / denom;
            }

            frames.Add(new PitchFrame(time, fs / period, Math.Clamp(1 - cmndf[best], 0, 1), rmsDb));
        }

        return frames;
    }

    private static float[] Decimate(float[] x, int factor)
    {
        if (factor == 1) return x;
        var y = new float[x.Length / factor];
        for (int i = 0; i < y.Length; i++)
        {
            float sum = 0;
            for (int k = 0; k < factor; k++) sum += x[i * factor + k];
            y[i] = sum / factor;
        }
        return y;
    }
}
