using NAudio.Wave;
using SoundMaster.Core.Audio;

namespace SoundMaster.Tests;

public class MixerEngineTests
{
    private static ChannelStrip ConstantStrip(float level, int frames = 4410, string name = "t")
    {
        var data = new float[frames * 2];
        Array.Fill(data, level);
        return new ChannelStrip(AudioTrack.FromFloat(name, data));
    }

    private static float[] Render(MixerEngine engine, int frames)
    {
        var buf = new float[frames * 2];
        engine.Play();
        engine.Read(buf, 0, buf.Length);
        return buf;
    }

    [Fact]
    public void PanLaw_CenterIsUnity_AndEdgesSilenceOneSide()
    {
        Assert.Equal((1f, 1f), PanLaw.Gains(0f));
        var left = PanLaw.Gains(-1f);
        Assert.Equal(1f, left.Left);
        Assert.True(left.Right < 1e-6f);
        var right = PanLaw.Gains(1f);
        Assert.True(right.Left < 1e-6f);
        Assert.Equal(1f, right.Right);
    }

    [Fact]
    public void Gain_ScalesOutput()
    {
        var engine = new MixerEngine();
        var strip = ConstantStrip(0.5f);
        strip.GainDb = -6.0206f; // ≈ 0.5
        engine.SetStrips([strip]);
        var buf = Render(engine, 2048);
        Assert.InRange(buf[2000], 0.2495f, 0.2505f);
    }

    [Fact]
    public void Mute_Silences_AndSoloIsolates()
    {
        var engine = new MixerEngine();
        var a = ConstantStrip(0.2f, name: "a");
        var b = ConstantStrip(0.4f, name: "b");
        engine.SetStrips([a, b]);

        Assert.InRange(Render(engine, 2048)[2000], 0.599f, 0.601f);

        engine.Seek(0);
        a.Muted = true;
        Assert.InRange(Render(engine, 2048)[2000], 0.399f, 0.401f);

        engine.Seek(0);
        a.Muted = false;
        a.Soloed = true;
        Assert.InRange(Render(engine, 2048)[2000], 0.199f, 0.201f);
    }

    [Fact]
    public void Limiter_NeverExceedsCeiling()
    {
        var engine = new MixerEngine();
        var strips = Enumerable.Range(0, 6).Select(i => ConstantStrip(0.9f, name: $"s{i}")).ToArray();
        engine.SetStrips(strips);
        var buf = Render(engine, 4000);
        float ceiling = MathF.Pow(10f, -0.3f / 20f);
        Assert.True(buf.Max(MathF.Abs) <= ceiling + 1e-5f);
    }

    [Fact]
    public void Read_WhenPaused_ReturnsSilence_AndKeepsPosition()
    {
        var engine = new MixerEngine();
        engine.SetStrips([ConstantStrip(0.5f)]);
        var buf = new float[1024];
        Array.Fill(buf, 1f);
        engine.Read(buf, 0, buf.Length);
        Assert.All(buf, v => Assert.Equal(0f, v));
        Assert.Equal(0, engine.PositionFrames);
    }

    [Fact]
    public void Playback_StopsAtEnd_AndSeekWorks()
    {
        var engine = new MixerEngine();
        engine.SetStrips([ConstantStrip(0.1f, frames: 1000)]);
        engine.Play();
        var buf = new float[4000];
        engine.Read(buf, 0, buf.Length);
        Assert.False(engine.IsPlaying);
        Assert.Equal(1000, engine.PositionFrames);
        Assert.Equal(0f, buf[3000]);

        engine.Seek(250);
        Assert.Equal(250, engine.PositionFrames);
    }

    [Fact]
    public void Meter_ReportsPeak_AndResets()
    {
        var engine = new MixerEngine();
        var strip = ConstantStrip(0.5f);
        engine.SetStrips([strip]);
        Render(engine, 1024);
        var (l, _) = strip.Meter.ReadAndReset();
        Assert.InRange(l, 0.49f, 0.51f);
        Assert.Equal(0f, strip.Meter.ReadAndReset().Left);
    }

    [Theory]
    [InlineData(ExportFormat.Pcm16, 16)]
    [InlineData(ExportFormat.Pcm24, 24)]
    [InlineData(ExportFormat.Float32, 32)]
    public void Export_WritesFileWithExpectedLengthAndFormat(ExportFormat format, int bits)
    {
        var engine = new MixerEngine();
        engine.SetStrips(DemoStems.Generate(seconds: 2).Select(t => new ChannelStrip(t)));
        string path = Path.Combine(Path.GetTempPath(), $"sm_test_{Guid.NewGuid():N}.wav");
        try
        {
            var result = engine.ExportWav(path, format);
            Assert.Equal(2 * EngineFormat.SampleRate, result.Frames);
            Assert.True(result.PeakDbfs <= 0f);

            using var reader = new WaveFileReader(path);
            Assert.Equal(bits, reader.WaveFormat.BitsPerSample);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(result.Frames, reader.SampleCount);
            Assert.Equal(0, engine.PositionFrames); // xuất không làm lệch vị trí phát
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Export_EqualsRealtimeRender()
    {
        var engine = new MixerEngine();
        engine.SetStrips(DemoStems.Generate(seconds: 1).Select(t => new ChannelStrip(t)));
        string path = Path.Combine(Path.GetTempPath(), $"sm_test_{Guid.NewGuid():N}.wav");
        try
        {
            engine.ExportWav(path, ExportFormat.Float32);
            using var reader = new AudioFileReader(path);
            var exported = new float[EngineFormat.SampleRate * 2];
            int n = ((ISampleProvider)reader).Read(exported);

            var live = new float[EngineFormat.SampleRate * 2];
            engine.Seek(0);
            engine.Play();
            engine.Read(live, 0, live.Length);

            Assert.Equal(live.Length, n);
            for (int i = 0; i < n; i++)
                Assert.Equal(live[i], exported[i], 1e-6f);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
