using SoundMaster.Core.Audio;
using Xunit;

namespace SoundMaster.Tests;

public class ClipEffectTests
{
    /// <summary>Stem mono-giống-stereo: giá trị mẫu i = (i+1)/N nên dễ đối chiếu vị trí.</summary>
    private static (MixerEngine Engine, ChannelStrip Strip, int Frames) Setup(int frames = 4000)
    {
        var data = new float[frames * 2];
        for (int i = 0; i < frames; i++) data[2 * i] = data[2 * i + 1] = 0.5f * (i + 1) / frames;
        var strip = new ChannelStrip(AudioTrack.FromFloat("ramp", data)) { GainDb = 0 };
        var engine = new MixerEngine { MasterGainDb = 0 };
        engine.Limiter.Enabled = false;
        engine.SetStrips([strip]);
        return (engine, strip, frames);
    }

    private static float MixAt(MixerEngine engine, int frame)
    {
        engine.Seek(frame);
        var buf = new float[2];
        engine.Play();
        engine.Read(buf, 0, 2);
        return buf[0];
    }

    private static float[] Render(MixerEngine engine, int frames)
    {
        var buf = new float[frames * 2];
        engine.Play();
        engine.Read(buf, 0, buf.Length);
        return buf;
    }

    [Fact]
    public void FadeIn_ramps_from_silence_to_full_over_the_range()
    {
        var (engine, strip, n) = Setup();
        strip.SetEffects([new ClipEffect(ClipEffectKind.FadeIn, 1000, 3000)]);
        var plain = Render(Setup().Engine, n);
        var faded = Render(engine, n);

        Assert.Equal(plain[2 * 500], faded[2 * 500], 3);               // ngoài đoạn: không đổi
        Assert.True(Math.Abs(faded[2 * 1000]) < 0.001f);                // đầu đoạn: im
        Assert.InRange(faded[2 * 2000] / plain[2 * 2000], 0.45f, 0.55f); // giữa: ~50%
        Assert.InRange(faded[2 * 2990] / plain[2 * 2990], 0.97f, 1.01f); // cuối: ~đầy
        Assert.Equal(plain[2 * 3500], faded[2 * 3500], 3);
    }

    [Fact]
    public void FadeOut_ramps_down_and_Silence_mutes()
    {
        var (engine, strip, n) = Setup();
        strip.SetEffects([
            new ClipEffect(ClipEffectKind.FadeOut, 500, 1500),
            new ClipEffect(ClipEffectKind.Silence, 2000, 3000),
        ]);
        var plain = Render(Setup().Engine, n);
        var fx = Render(engine, n);

        Assert.InRange(fx[2 * 1000] / plain[2 * 1000], 0.45f, 0.55f);
        Assert.True(Math.Abs(fx[2 * 1499]) < 0.002f);
        Assert.Equal(0f, fx[2 * 2500], 4);                              // giữa đoạn im lặng
        Assert.Equal(plain[2 * 3500], fx[2 * 3500], 3);                 // sau đoạn: khôi phục
    }

    [Fact]
    public void Reverse_plays_the_range_backwards()
    {
        var (engine, strip, n) = Setup();
        strip.SetEffects([new ClipEffect(ClipEffectKind.Reverse, 1000, 2000)]);
        var plain = Render(Setup().Engine, n);
        var rev = Render(engine, n);

        Assert.Equal(plain[2 * 1999], rev[2 * 1000], 3);  // đầu đoạn = mẫu cuối gốc
        Assert.Equal(plain[2 * 1000], rev[2 * 1999], 3);
        Assert.Equal(plain[2 * 500], rev[2 * 500], 3);
        Assert.Equal(plain[2 * 2500], rev[2 * 2500], 3);
    }

    [Fact]
    public void LoopRegion_wraps_playback_and_can_be_cleared()
    {
        var (engine, _, _) = Setup();
        engine.SetLoopRegion(1000, 1500);
        engine.Seek(1000);
        Render(engine, 2600); // 5 lần đoạn dài 500 + 100
        Assert.InRange(engine.PositionFrames, 1000, 1500);
        Assert.Equal(1100, engine.PositionFrames);

        engine.ClearLoopRegion();
        Assert.Null(engine.LoopRegion);
        engine.Seek(1000);
        Render(engine, 1000);
        Assert.Equal(2000, engine.PositionFrames);
    }

    [Fact]
    public void Effects_are_applied_when_exporting_too()
    {
        var (engine, strip, _) = Setup();
        strip.SetEffects([new ClipEffect(ClipEffectKind.Silence, 100, 3000)]);
        string path = Path.Combine(Path.GetTempPath(), $"sm_fx_{Guid.NewGuid():N}.wav");
        try
        {
            var result = engine.ExportWav(path, ExportFormat.Float32);
            // Không có hiệu ứng thì đỉnh = -6 dBFS (cuối ramp); im lặng đoạn 100..3000 thì đỉnh chỉ còn ở phần sau.
            Assert.InRange(result.PeakDbfs, -6.2, -5.8);
            Assert.Equal(0f, MixAt(engine, 1500), 4);
        }
        finally { File.Delete(path); }
    }
}
