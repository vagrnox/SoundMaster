using NAudio.Wave;
using SoundMaster.Core.Audio;

namespace SoundMaster.Tests;

/// <summary>
/// Chẩn đoán thiết bị âm thanh thật. Chỉ chạy khi đặt SOUNDMASTER_AUDIO_TEST=1
/// (sẽ phát khoảng 1 giây tiếng nhỏ ra loa mặc định).
/// </summary>
public class AudioDeviceTests
{
    private sealed class TapProvider(ISampleProvider inner) : ISampleProvider
    {
        public int Reads;
        public long Frames;
        public float MaxAbs;
        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int n = inner.Read(buffer);
            Interlocked.Increment(ref Reads);
            Interlocked.Add(ref Frames, n / 2);
            foreach (float v in buffer[..n])
            {
                float a = MathF.Abs(v);
                if (a > MaxAbs) MaxAbs = a;
            }
            return n;
        }
    }

    [Fact]
    public void RealDevice_PullsNonSilentAudio_WhenPlaying()
    {
        if (Environment.GetEnvironmentVariable("SOUNDMASTER_AUDIO_TEST") != "1") return;

        var engine = new MixerEngine { MasterGainDb = -30 };
        engine.SetStrips(DemoStems.Generate(seconds: 4).Select(t => new ChannelStrip(t)));
        var tap = new TapProvider(engine);

        using var output = new AudioOutput(tap);
        Thread.Sleep(300);
        long framesWhileStopped = tap.Frames;
        float maxWhileStopped = tap.MaxAbs;

        engine.Play();
        Thread.Sleep(1200);
        engine.Pause();

        string report = $"backend='{output.Description}' WaveOut.DeviceCount={WaveOut.DeviceCount} reads={tap.Reads} frames={tap.Frames} " +
                        $"maxAbs={tap.MaxAbs:0.0000} position={engine.PositionFrames} " +
                        $"framesBeforePlay={framesWhileStopped} maxBeforePlay={maxWhileStopped:0.0000}";

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "soundmaster_audio_report.txt"), report);

        Assert.True(WaveOut.DeviceCount > 0, "Không có thiết bị WaveOut. " + report);
        Assert.True(tap.Reads > 0, "Thiết bị không hề lấy dữ liệu. " + report);
        Assert.True(engine.PositionFrames > EngineFormat.SampleRate / 2, "Vị trí phát không tiến. " + report);
        Assert.True(tap.MaxAbs > 0.001f, "Dữ liệu gửi ra thiết bị toàn im lặng. " + report);
    }
}
