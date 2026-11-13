using NAudio.Wave;
using SoundMaster.Core.Audio;
using Xunit;

namespace SoundMaster.Tests;

public class TimeStretchTests
{
    /// <summary>Nguồn sine stereo liên tục, đếm số frame đã bị kéo đi.</summary>
    private sealed class SineSource(double freq) : ISampleProvider
    {
        public long FramesRead;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<float> buffer)
        {
            for (int i = 0; i < buffer.Length / 2; i++)
            {
                float v = (float)(0.5 * Math.Sin(2 * Math.PI * freq * (FramesRead + i) / 44100));
                buffer[2 * i] = v;
                buffer[2 * i + 1] = v;
            }
            FramesRead += buffer.Length / 2;
            return buffer.Length;
        }
    }

    private static float[] Render(TimeStretchProvider p, int frames)
    {
        var all = new float[frames * 2];
        for (int off = 0; off < all.Length; off += 1024) // khối nhỏ như thiết bị âm thanh
            p.Read(all.AsSpan(off, Math.Min(1024, all.Length - off)));
        return all;
    }

    private static double Frequency(float[] stereo, int skipFrames)
    {
        int crossings = 0;
        int frames = stereo.Length / 2;
        for (int i = skipFrames + 1; i < frames; i++)
            if (stereo[2 * (i - 1)] < 0 && stereo[2 * i] >= 0) crossings++;
        return crossings / ((frames - skipFrames) / 44100.0);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Speed_changes_consumption_but_keeps_pitch(double speed)
    {
        var src = new SineSource(440);
        var p = new TimeStretchProvider(src) { Speed = speed };
        const int outFrames = 44100 * 3;
        var output = Render(p, outFrames);

        // Tiêu thụ nguồn ≈ speed × số frame phát ra (cộng độ trễ đệm nhỏ).
        double ratio = src.FramesRead / (double)outFrames;
        Assert.InRange(ratio, speed * 0.97, speed * 1.03 + 0.05);

        // Cao độ vẫn ≈ 440 Hz.
        Assert.InRange(Frequency(output, 4096), 425, 455);
        Assert.Contains(output, v => Math.Abs(v) > 0.3f);
    }

    [Fact]
    public void Speed_one_is_passthrough()
    {
        var a = new SineSource(440);
        var b = new SineSource(440);
        var p = new TimeStretchProvider(a);
        var got = Render(p, 4096);
        var expected = new float[4096 * 2];
        b.Read(expected.AsSpan());
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Reset_discards_buffered_audio_and_speed_is_clamped()
    {
        var src = new SineSource(440);
        var p = new TimeStretchProvider(src) { Speed = 2 };
        Render(p, 4096);
        p.Reset();
        p.Speed = 99;
        Assert.Equal(TimeStretchProvider.MaxSpeed, p.Speed, 5);
        p.Speed = 0;
        Assert.Equal(TimeStretchProvider.MinSpeed, p.Speed, 5);
        // Vẫn render được sau khi reset, không ném lỗi.
        Render(p, 4096);
    }
}
