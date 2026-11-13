using NAudio.MediaFoundation;
using NAudio.Wave;
using SoundMaster.Core.Audio;

namespace SoundMaster.Tests;

/// <summary>Kiểm tra giải mã file thật (AudioTrack.Load): đổi mono → stereo, resample, độ sâu bit.</summary>
public class AudioTrackLoadTests
{
    private static string WriteSineWav(WaveFormat format, int channels, double seconds, double amplitude)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sm_load_{Guid.NewGuid():N}.wav");
        int frames = (int)(seconds * format.SampleRate);
        using var writer = new WaveFileWriter(path, format);
        var frame = new float[channels];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(Math.Sin(2 * Math.PI * 440 * i / format.SampleRate) * amplitude);
            Array.Fill(frame, v);
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                writer.WriteSamples(frame, 0, channels);
            }
            else
            {
                foreach (float s in frame)
                {
                    if (format.BitsPerSample == 16)
                    {
                        short q = (short)Math.Round(s * 32767);
                        writer.Write([(byte)q, (byte)(q >> 8)]);
                    }
                    else // 24-bit
                    {
                        int q = (int)Math.Round(s * 8388607);
                        writer.Write([(byte)q, (byte)(q >> 8), (byte)(q >> 16)]);
                    }
                }
            }
        }
        return path;
    }

    private static float MaxPeak(AudioTrack t) => t.Peaks.Length == 0 ? 0 : t.Peaks.Max();

    [Theory]
    [InlineData(16, 44100, 2)]
    [InlineData(16, 44100, 1)]
    [InlineData(16, 48000, 2)]
    [InlineData(24, 44100, 2)]
    [InlineData(24, 48000, 1)]
    public void Load_Pcm_ProducesCorrectLengthAndLevel(int bits, int rate, int channels)
    {
        string path = WriteSineWav(new WaveFormat(rate, bits, channels), channels, seconds: 2, amplitude: 0.5);
        try
        {
            var track = AudioTrack.Load(path);
            Assert.InRange(track.FrameCount, 2 * EngineFormat.SampleRate - 400, 2 * EngineFormat.SampleRate + 400);
            Assert.InRange(MaxPeak(track), 0.45f, 0.55f);

            // Hai kênh phải giống nhau (nguồn mono hoặc stereo giống nhau).
            int mid = track.FrameCount / 2;
            Assert.Equal(track.Samples[2 * mid], track.Samples[2 * mid + 1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_Float32_ProducesCorrectLengthAndLevel()
    {
        string path = WriteSineWav(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2), 2, seconds: 2, amplitude: 0.5);
        try
        {
            var track = AudioTrack.Load(path);
            Assert.InRange(track.FrameCount, 2 * EngineFormat.SampleRate - 400, 2 * EngineFormat.SampleRate + 400);
            Assert.InRange(MaxPeak(track), 0.45f, 0.55f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void Load_CompressedFormats_ProduceCorrectLengthAndLevel(string ext)
    {
        // Tạo file nén bằng bộ mã hóa Media Foundation của Windows; bỏ qua nếu máy không có.
        string wav = WriteSineWav(new WaveFormat(44100, 16, 2), 2, seconds: 3, amplitude: 0.5);
        string compressed = Path.ChangeExtension(wav, ext);
        try
        {
            try
            {
                MediaFoundationApi.Startup();
                using var source = new WaveFileReader(wav);
                if (ext == "mp3") MediaFoundationEncoder.EncodeToMp3(source, compressed, 192000);
                else MediaFoundationEncoder.EncodeToAac(source, compressed, 192000);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                           or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), $"soundmaster_{ext}_skipped.txt"), ex.ToString());
                return;
            }

            var track = AudioTrack.Load(compressed);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"soundmaster_{ext}_report.txt"),
                $"frames={track.FrameCount} maxPeak={MaxPeak(track):0.000}");

            // Bộ mã hóa thêm vài chục ms đệm; biên độ giữ trong ±15%.
            Assert.InRange(track.FrameCount, 3 * EngineFormat.SampleRate - 5000, 3 * EngineFormat.SampleRate + 5000);
            Assert.InRange(MaxPeak(track), 0.4f, 0.62f);
        }
        finally
        {
            File.Delete(wav);
            File.Delete(compressed);
        }
    }

    [Fact]
    public void Load_RealWindowsSoundFiles_AreNotSilent()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
        if (!Directory.Exists(dir)) return;

        var files = Directory.EnumerateFiles(dir, "*.wav").Take(6).ToList();
        foreach (var file in files)
        {
            var track = AudioTrack.Load(file);
            Assert.True(track.FrameCount > 0, $"{file} giải mã ra 0 frame");
            Assert.True(MaxPeak(track) > 0.01f, $"{file} giải mã ra im lặng");
        }
    }
}
