using SoundMaster.Core.Analysis;
using Xunit;

namespace SoundMaster.Tests;

public class MixAnalyzerTests
{
    private const int Fs = 44100;

    private static float[] Tone(double seconds, double amp, params double[] freqs)
    {
        var x = new float[(int)(seconds * Fs)];
        for (int i = 0; i < x.Length; i++)
        {
            double v = 0;
            foreach (var f in freqs) v += Math.Sin(2 * Math.PI * f * i / Fs);
            x[i] = (float)(amp * v / freqs.Length);
        }
        return x;
    }

    [Fact]
    public void Loudness_of_minus23dBFS_997Hz_stereo_is_about_minus23_LUFS()
    {
        var x = Tone(5, Math.Pow(10, -23 / 20.0), 997);
        double? lufs = Loudness.Integrated(x, x, Fs);
        Assert.NotNull(lufs);
        Assert.InRange(lufs!.Value, -23.3, -22.7);
    }

    [Fact]
    public void Loudness_of_silence_is_null()
    {
        var x = new float[Fs * 3];
        Assert.Null(Loudness.Integrated(x, x, Fs));
    }

    [Fact]
    public void Loudness_gate_ignores_quiet_passages()
    {
        // 3 s ồn -20 dBFS + 3 s gần như im lặng (-80): kết quả phải gần mức của phần ồn.
        var loud = Tone(3, Math.Pow(10, -20 / 20.0), 997);
        var quiet = Tone(3, Math.Pow(10, -80 / 20.0), 997);
        var x = loud.Concat(quiet).ToArray();
        double lufs = Loudness.Integrated(x, x, Fs)!.Value;
        Assert.InRange(lufs, -20.3, -19.7);
    }

    [Theory]
    [InlineData("C", true, new[] { 261.63, 329.63, 392.00, 130.81 })]
    [InlineData("G#", true, new[] { 207.65, 261.63, 311.13, 103.83 })]
    [InlineData("A", false, new[] { 220.00, 261.63, 329.63, 110.00 })]
    public void KeyDetector_finds_the_key_of_a_sustained_triad(string key, bool major, double[] freqs)
    {
        var x = Tone(12, 0.3, freqs);
        var (k, isMajor, conf) = KeyDetector.Detect(x, x, Fs);
        Assert.Equal(key, k);
        Assert.Equal(major, isMajor);
        Assert.True(conf > 0.5);
    }

    [Fact]
    public void Analyze_reports_peak_and_texts()
    {
        var x = Tone(5, 0.5, 440);
        var a = MixAnalyzer.Analyze(x, x, Fs);
        Assert.InRange(a.PeakDb, -6.2, -5.8);
        Assert.False(string.IsNullOrEmpty(a.KeyText));
        Assert.StartsWith("Peak", a.PeakText);
    }
}

public class TempoAnalyzerTests
{
    private const int Fs = 44100;

    /// <summary>Tiếng "kick" (sine 80 Hz tắt dần) đặt tại các thời điểm cho trước.</summary>
    private static float[] Clicks(double seconds, IEnumerable<double> times)
    {
        var x = new float[(int)(seconds * Fs)];
        foreach (double t in times)
        {
            int s = (int)(t * Fs);
            for (int i = 0; i < 4000 && s + i < x.Length; i++)
                x[s + i] += (float)(0.8 * Math.Exp(-i / 800.0) * Math.Sin(2 * Math.PI * 80 * i / Fs));
        }
        return x;
    }

    private static IEnumerable<double> Grid(double bpm, double from, double to)
    {
        for (double t = from; t < to; t += 60.0 / bpm) yield return t;
    }

    [Fact]
    public void Steady_120bpm_is_detected_and_very_stable()
    {
        var x = Clicks(40, Grid(120, 0.1, 40));
        var r = TempoAnalyzer.Analyze(x, x, Fs);
        Assert.NotNull(r);
        Assert.InRange(r!.Bpm, 118, 122);
        Assert.NotNull(r.Stability);
        Assert.True(r.Stability > 0.9, $"stability {r.Stability}");
    }

    [Fact]
    public void Tempo_change_lowers_stability()
    {
        var times = Grid(100, 0.1, 20).Concat(Grid(140, 20, 40));
        var x = Clicks(40, times);
        var r = TempoAnalyzer.Analyze(x, x, Fs);
        Assert.NotNull(r);
        Assert.NotNull(r!.Stability);
        Assert.True(r.Stability < 0.8, $"stability {r.Stability}");
    }

    [Fact]
    public void Unsteady_playing_scores_lower_than_steady()
    {
        var rng = new Random(1);
        var jitter = Grid(120, 0.1, 40).Select(t => t + (rng.NextDouble() - 0.5) * 0.12);
        var steady = TempoAnalyzer.Analyze(Clicks(40, Grid(120, 0.1, 40)), Clicks(40, Grid(120, 0.1, 40)), Fs)!;
        var x = Clicks(40, jitter);
        var loose = TempoAnalyzer.Analyze(x, x, Fs);
        Assert.True(loose is null || loose.Stability is null || loose.Stability < steady.Stability);
    }

    [Fact]
    public void Too_short_or_silent_audio_returns_null()
    {
        var shortX = Clicks(3, Grid(120, 0.1, 3));
        Assert.Null(TempoAnalyzer.Analyze(shortX, shortX, Fs));
        var silent = new float[Fs * 20];
        Assert.Null(TempoAnalyzer.Analyze(silent, silent, Fs));
    }

    [Fact]
    public void DynamicRange_is_peak_minus_lufs()
    {
        var a = new MixAnalysis(-13.4, 1.9, "C", true, 1);
        Assert.Equal(15.3, a.DynamicRangeDb!.Value, 6);
        Assert.Equal("Wide", a.DynamicRangeLabel);
        Assert.Null(MixAnalysis.Empty.DynamicRangeDb);
    }
}

public class PanBarTests
{
    [Fact]
    public void Gradient_gets_stronger_on_the_side_you_pan_towards()
    {
        var (l0, r0) = SoundMaster.App.Controls.PanBar.SideAlphas(0);
        Assert.Equal(l0, r0, 6);

        var (l, r) = SoundMaster.App.Controls.PanBar.SideAlphas(-1);
        Assert.True(l > r);
        Assert.True(l > l0);
        Assert.Equal(r0, r, 6); // phía còn lại giữ nguyên độ mờ

        var (l2, r2) = SoundMaster.App.Controls.PanBar.SideAlphas(0.5);
        Assert.True(r2 > l2);
        Assert.True(r2 < SoundMaster.App.Controls.PanBar.SideAlphas(1).Right);
    }
}
