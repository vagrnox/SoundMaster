using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Một stem đã giải mã sẵn trong bộ nhớ: stereo interleaved, 44.1 kHz, lưu Int16
/// để 12 stem không chiếm quá nhiều RAM (~42 MB/phút/stem thay vì ~85 MB).
/// </summary>
public sealed class AudioTrack
{
    private const float Int16Scale = 1f / 32768f;

    public AudioTrack(string name, short[] samples, int frameCount)
    {
        Name = name;
        Samples = samples;
        FrameCount = frameCount;
    }

    public string Name { get; }
    public short[] Samples { get; }
    public int FrameCount { get; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / EngineFormat.SampleRate);

    public static AudioTrack FromFloat(string name, float[] interleavedStereo)
    {
        var data = new short[interleavedStereo.Length];
        for (int i = 0; i < data.Length; i++)
            data[i] = ToInt16(interleavedStereo[i]);
        return new AudioTrack(name, data, data.Length / 2);
    }

    public static AudioTrack Load(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;

        if (source.WaveFormat.Channels == 1)
        {
            source = new MonoToStereoSampleProvider(source);
        }
        else if (source.WaveFormat.Channels > 2)
        {
            var mux = new MultiplexingSampleProvider(new[] { source }, 2);
            mux.ConnectInputToOutput(0, 0);
            mux.ConnectInputToOutput(1, 1);
            source = mux;
        }

        if (source.WaveFormat.SampleRate != EngineFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, EngineFormat.SampleRate);

        int estimatedFrames = (int)(reader.TotalTime.TotalSeconds * EngineFormat.SampleRate) + EngineFormat.SampleRate;
        var samples = new short[estimatedFrames * 2];
        var chunk = new float[8192 * 2];
        int written = 0;

        int read;
        while ((read = source.Read(chunk)) > 0)
        {
            if (written + read > samples.Length)
                Array.Resize(ref samples, Math.Max(samples.Length * 2, written + read));
            for (int i = 0; i < read; i++)
                samples[written + i] = ToInt16(chunk[i]);
            written += read;
        }

        return new AudioTrack(Path.GetFileNameWithoutExtension(path), samples, written / 2);
    }

    public (float Left, float Right) GetFrame(long frame)
    {
        long idx = frame * 2;
        return (Samples[idx] * Int16Scale, Samples[idx + 1] * Int16Scale);
    }

    private static short ToInt16(float v)
        => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);
}
