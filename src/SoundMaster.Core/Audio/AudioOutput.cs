using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Đẩy một <see cref="ISampleProvider"/> (thường là <see cref="MixerEngine"/>) ra loa mặc định.
/// Ưu tiên WASAPI shared; nếu không mở được thì dùng WaveOut. Thiết bị chạy liên tục,
/// engine tự phát im lặng khi tạm dừng nên Play/Pause phản hồi nhanh.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioOutput : IDisposable
{
    private readonly IWavePlayer _device;

    public AudioOutput(ISampleProvider source, int latencyMs = 80)
    {
        string? wasapiError = null;
        IWavePlayer? device = null;
        string description = "";

        try
        {
            var wasapi = new WasapiOut(AudioClientShareMode.Shared, latencyMs);
            try
            {
                wasapi.Init(source);
                wasapi.Play();
            }
            catch
            {
                wasapi.Dispose();
                throw;
            }

            device = wasapi;
            description = $"WASAPI · {DefaultRenderDeviceName()}";
        }
        catch (Exception ex)
        {
            wasapiError = ex.Message;
        }

        if (device is null)
        {
            var waveOut = new WaveOut { BufferMilliseconds = 40, NumberOfBuffers = 3 };
            try
            {
                waveOut.Init(source);
                waveOut.Play();
            }
            catch
            {
                waveOut.Dispose();
                throw;
            }

            device = waveOut;
            description = $"WaveOut (WASAPI lỗi: {wasapiError})";
        }

        _device = device;
        Description = description;
    }

    /// <summary>Mô tả backend và thiết bị đang dùng, để hiển thị cho người dùng.</summary>
    public string Description { get; }

    private static string DefaultRenderDeviceName()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch
        {
            return "thiết bị mặc định";
        }
    }

    public void Dispose() => _device.Dispose();
}
