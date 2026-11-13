using System.Runtime.Versioning;
using NAudio.Wave;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Đẩy <see cref="MixerEngine"/> ra loa mặc định. Thiết bị chạy liên tục;
/// engine tự phát im lặng khi tạm dừng, nên Play/Pause phản hồi nhanh.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioOutput : IDisposable
{
    private readonly WaveOut _device;

    public AudioOutput(MixerEngine engine, int bufferMs = 40)
    {
        _device = new WaveOut { BufferMilliseconds = bufferMs, NumberOfBuffers = 3 };
        _device.Init(engine);
        _device.Play();
    }

    public void Dispose() => _device.Dispose();
}
