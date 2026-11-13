namespace SoundMaster.Core.Audio;

/// <summary>Sinh vài stem tổng hợp (kick, bass, hat, pad, lead) để thử mixer khi chưa có file Suno.</summary>
public static class DemoStems
{
    public static IReadOnlyList<AudioTrack> Generate(double seconds = 16, double bpm = 120)
    {
        int sr = EngineFormat.SampleRate;
        int frames = (int)(seconds * sr);
        double beat = 60.0 / bpm;
        int beats = (int)(seconds / beat);

        return
        [
            AudioTrack.FromFloat("Kick", Kick(frames, sr, beat, beats)),
            AudioTrack.FromFloat("Bass", Bass(frames, sr, beat, beats)),
            AudioTrack.FromFloat("Hi-hat", HiHat(frames, sr, beat, beats)),
            AudioTrack.FromFloat("Pad", Pad(frames, sr)),
            AudioTrack.FromFloat("Lead", Lead(frames, sr, beat, beats)),
        ];
    }

    private static float[] Kick(int frames, int sr, double beat, int beats)
    {
        var buf = new float[frames * 2];
        for (int b = 0; b < beats; b++)
        {
            int start = (int)(b * beat * sr);
            double phase = 0;
            for (int i = 0; i < sr / 4 && start + i < frames; i++)
            {
                double t = (double)i / sr;
                phase += 2 * Math.PI * (45 + 90 * Math.Exp(-t * 25)) / sr;
                float v = (float)(Math.Sin(phase) * Math.Exp(-t * 9) * 0.9);
                buf[2 * (start + i)] += v;
                buf[2 * (start + i) + 1] += v;
            }
        }
        return buf;
    }

    private static float[] Bass(int frames, int sr, double beat, int beats)
    {
        double[] notes = [55.0, 55.0, 65.41, 49.0];
        var buf = new float[frames * 2];
        for (int b = 0; b < beats; b += 2)
        {
            double f = notes[(b / 2) % notes.Length];
            int start = (int)(b * beat * sr);
            int len = (int)(2 * beat * sr * 0.9);
            for (int i = 0; i < len && start + i < frames; i++)
            {
                double t = (double)i / sr;
                double env = Math.Min(1, t / 0.005) * Math.Exp(-t * 1.2);
                double p = 2 * Math.PI * f * t;
                float v = (float)((Math.Sin(p) + 0.3 * Math.Sin(2 * p)) * env * 0.5);
                buf[2 * (start + i)] += v;
                buf[2 * (start + i) + 1] += v;
            }
        }
        return buf;
    }

    private static float[] HiHat(int frames, int sr, double beat, int beats)
    {
        var rng = new Random(1);
        var buf = new float[frames * 2];
        for (int e = 0; e < beats * 2; e++)
        {
            int start = (int)(e * beat / 2 * sr);
            double prev = 0;
            for (int i = 0; i < sr / 25 && start + i < frames; i++)
            {
                double n = rng.NextDouble() * 2 - 1;
                double hp = n - prev; // high-pass thô bằng sai phân bậc 1
                prev = n;
                float v = (float)(hp * Math.Exp(-(double)i / sr * 80) * 0.25);
                buf[2 * (start + i)] += v * 0.6f;
                buf[2 * (start + i) + 1] += v;
            }
        }
        return buf;
    }

    private static float[] Pad(int frames, int sr)
    {
        double[] chord = [220.0, 261.63, 329.63];
        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / sr;
            double tremolo = 0.75 + 0.25 * Math.Sin(2 * Math.PI * 0.25 * t);
            double l = 0, r = 0;
            foreach (double f in chord)
            {
                l += Math.Sin(2 * Math.PI * f * t);
                r += Math.Sin(2 * Math.PI * f * 1.002 * t);
            }
            buf[2 * i] = (float)(l * 0.12 * tremolo);
            buf[2 * i + 1] = (float)(r * 0.12 * tremolo);
        }
        return buf;
    }

    private static float[] Lead(int frames, int sr, double beat, int beats)
    {
        double[] scale = [440.0, 523.25, 587.33, 659.25, 784.0];
        int[] pattern = [0, 2, 3, 2, 4, 3, 1, 0];
        var buf = new float[frames * 2];
        for (int b = 0; b < beats; b++)
        {
            double f = scale[pattern[b % pattern.Length]];
            int start = (int)(b * beat * sr);
            for (int i = 0; i < (int)(beat * sr) && start + i < frames; i++)
            {
                double t = (double)i / sr;
                float v = (float)(Math.Sin(2 * Math.PI * f * t) * Math.Exp(-t * 3) * 0.3);
                buf[2 * (start + i)] += v;
                buf[2 * (start + i) + 1] += v * 0.5f;
            }
        }
        return buf;
    }
}
