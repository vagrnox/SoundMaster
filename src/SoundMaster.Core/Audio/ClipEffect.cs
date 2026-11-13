namespace SoundMaster.Core.Audio;

/// <summary>Hiệu ứng áp lên một đoạn của một track (render cả khi phát lẫn khi xuất file).</summary>
public enum ClipEffectKind
{
    /// <summary>Âm lượng tăng tuyến tính từ 0 lên 1 trong đoạn.</summary>
    FadeIn,

    /// <summary>Âm lượng giảm tuyến tính từ 1 về 0 trong đoạn.</summary>
    FadeOut,

    /// <summary>Tắt tiếng đoạn (vào/ra mượt 5 ms để không bị tiếng rắc).</summary>
    Silence,

    /// <summary>Phát ngược đoạn.</summary>
    Reverse,
}

/// <summary>Một hiệu ứng trên đoạn [StartFrame, EndFrame) của stem.</summary>
public sealed record ClipEffect(ClipEffectKind Kind, long StartFrame, long EndFrame)
{
    private const int SilenceRampFrames = EngineFormat.SampleRate / 200; // 5 ms

    /// <summary>
    /// Với vị trí đầu ra <paramref name="pos"/> trả về frame nguồn cần đọc và hệ số gain.
    /// Các hiệu ứng chồng nhau thì nhân gain; Reverse đầu tiên quyết định frame nguồn.
    /// </summary>
    internal static (long Source, float Gain) Resolve(ClipEffect[] effects, long pos)
    {
        long source = pos;
        float gain = 1f;
        bool reversed = false;

        foreach (var e in effects)
        {
            if (pos < e.StartFrame || pos >= e.EndFrame) continue;
            long length = e.EndFrame - e.StartFrame;
            switch (e.Kind)
            {
                case ClipEffectKind.FadeIn:
                    gain *= (float)(pos - e.StartFrame) / length;
                    break;
                case ClipEffectKind.FadeOut:
                    gain *= (float)(e.EndFrame - 1 - pos) / length;
                    break;
                case ClipEffectKind.Silence:
                    long edge = Math.Min(pos - e.StartFrame, e.EndFrame - 1 - pos);
                    gain *= 1f - Math.Clamp((float)edge / SilenceRampFrames, 0f, 1f);
                    break;
                case ClipEffectKind.Reverse when !reversed:
                    source = e.StartFrame + e.EndFrame - 1 - pos;
                    reversed = true;
                    break;
            }
        }
        return (source, gain);
    }
}
