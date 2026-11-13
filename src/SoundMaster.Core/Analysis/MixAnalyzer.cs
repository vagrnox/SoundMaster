using System.Globalization;
using SoundMaster.Core.Audio;

namespace SoundMaster.Core.Analysis;

/// <summary>Kết quả phân tích bản mix (cộng các stem ở mức gốc, không tính fader).</summary>
/// <param name="Lufs">Độ ồn tích hợp theo ITU-R BS.1770-4 (LUFS); null nếu quá nhỏ/không đủ dữ liệu.</param>
/// <param name="PeakDb">Đỉnh mẫu (dBFS).</param>
/// <param name="Key">Tên giọng, ví dụ "G#"; null nếu không xác định.</param>
/// <param name="IsMajor">true = trưởng, false = thứ.</param>
/// <param name="KeyConfidence">0..1 (độ tương quan tốt nhất, đã chặn).</param>
/// <param name="TempoBpm">Tempo dò từ audio; null nếu không có nhịp đều đủ rõ.</param>
/// <param name="TempoStability">0..1; null nếu không đủ dữ liệu để đo.</param>
public sealed record MixAnalysis(
    double? Lufs, double PeakDb, string? Key, bool IsMajor, double KeyConfidence,
    double? TempoBpm = null, double? TempoStability = null)
{
    public static MixAnalysis Empty { get; } = new(null, double.NegativeInfinity, null, true, 0);

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public string KeyText => Key is null ? "—" : $"{Key} {(IsMajor ? "maj" : "min")}";
    public string ScaleText => Key is null ? "" : IsMajor ? "Major" : "Minor";
    public string LufsText => Lufs is { } l ? F1(l) : "—";
    public string PeakText => double.IsFinite(PeakDb) ? $"Peak {F1(PeakDb)} dB" : "";

    /// <summary>Dynamic range = đỉnh − độ ồn tích hợp (PLR), tính bằng dB.</summary>
    public double? DynamicRangeDb => Lufs is { } l && double.IsFinite(PeakDb) ? PeakDb - l : null;
    public string DynamicRangeText => DynamicRangeDb is { } d ? F1(d) : "—";
    public string DynamicRangeLabel => DynamicRangeDb switch
    {
        null => "",
        < 6 => "Compressed",
        < 10 => "Narrow",
        < 14 => "Moderate",
        _ => "Wide",
    };

    public string TempoStabilityText => TempoStability is { } s ? $"{Math.Round(s * 100):0}%" : "—";
    public string TempoStabilityLabel => TempoStability switch
    {
        null => TempoBpm is null ? "" : "Not enough data",
        >= 0.95 => "Very Stable",
        >= 0.85 => "Stable",
        >= 0.70 => "Moderate",
        _ => "Unstable",
    };
}

public static class MixAnalyzer
{
    public static MixAnalysis Analyze(IReadOnlyList<AudioTrack> tracks, CancellationToken ct = default)
    {
        int frames = tracks.Count == 0 ? 0 : tracks.Max(t => t.FrameCount);
        if (frames == 0) return MixAnalysis.Empty;

        var left = new float[frames];
        var right = new float[frames];
        const float scale = 1f / 32768f;
        foreach (var t in tracks)
        {
            ct.ThrowIfCancellationRequested();
            var s = t.Samples;
            for (int f = 0; f < t.FrameCount; f++)
            {
                left[f] += s[2 * f] * scale;
                right[f] += s[2 * f + 1] * scale;
            }
        }

        return Analyze(left, right, EngineFormat.SampleRate, ct);
    }

    public static MixAnalysis Analyze(float[] left, float[] right, int sampleRate, CancellationToken ct = default)
    {
        double peak = 0;
        for (int i = 0; i < left.Length; i++)
            peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        double peakDb = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;

        double? lufs = Loudness.Integrated(left, right, sampleRate);
        ct.ThrowIfCancellationRequested();
        var (key, major, conf) = KeyDetector.Detect(left, right, sampleRate, ct);
        var tempo = TempoAnalyzer.Analyze(left, right, sampleRate, ct);
        return new MixAnalysis(lufs, peakDb, key, major, conf, tempo?.Bpm, tempo?.Stability);
    }
}
