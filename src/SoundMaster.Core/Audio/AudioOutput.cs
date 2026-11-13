using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Đẩy một <see cref="ISampleProvider"/> (thường là <see cref="MixerEngine"/>) ra thiết bị phát mặc định.
/// Ưu tiên WASAPI shared; nếu không mở được thì dùng WaveOut. Thiết bị chạy liên tục,
/// engine tự phát im lặng khi tạm dừng nên Play/Pause phản hồi nhanh.
/// <para>
/// Tự đi theo thiết bị mặc định: khi người dùng bật/tắt tai nghe Bluetooth, cắm/rút tai nghe hoặc đổi
/// thiết bị mặc định trong Windows, luồng phát được mở lại trên thiết bị mới (không cần khởi động lại app).
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioOutput : IDisposable
{
    /// <summary>Chờ các thông báo thiết bị dồn lại (Bluetooth bắn nhiều sự kiện liên tiếp) rồi mới mở lại.</summary>
    private const int DebounceMs = 600;

    /// <summary>Thiết bị Bluetooth vừa kết nối có thể chưa sẵn sàng ngay: thử lại vài lần.</summary>
    private const int RetryMs = 1500;
    private const int MaxRetries = 6;

    private readonly ISampleProvider _source;
    private readonly int _latencyMs;
    private readonly object _gate = new();
    private readonly Timer _reopenTimer;

    private IWavePlayer? _device;
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _notifications;
    private string _description = "";
    private int _retries;
    private bool _disposed;

    /// <summary>Báo khi thiết bị thay đổi: mô tả mới và lỗi (null nếu mở thành công). Gọi từ luồng nền.</summary>
    public event Action<string, string?>? DeviceChanged;

    public AudioOutput(ISampleProvider source, int latencyMs = 80)
    {
        _source = source;
        _latencyMs = latencyMs;
        _reopenTimer = new Timer(_ => Reopen(), null, Timeout.Infinite, Timeout.Infinite);

        lock (_gate) OpenDevice(); // lần đầu: nếu lỗi thì ném ra cho người gọi
        StartWatching();
    }

    /// <summary>Mô tả backend và thiết bị đang dùng, để hiển thị cho người dùng.</summary>
    public string Description
    {
        get { lock (_gate) return _description; }
    }

    // ---------- mở / mở lại thiết bị ----------

    private void OpenDevice()
    {
        string? wasapiError = null;
        IWavePlayer? device = null;
        string description = "";

        try
        {
            var wasapi = new WasapiOut(AudioClientShareMode.Shared, _latencyMs);
            try
            {
                wasapi.Init(_source);
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
                waveOut.Init(_source);
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

        device.PlaybackStopped += OnPlaybackStopped;
        _device = device;
        _description = description;
    }

    private void CloseDevice()
    {
        var device = _device;
        _device = null;
        if (device is null) return;
        device.PlaybackStopped -= OnPlaybackStopped;
        try { device.Stop(); } catch { /* thiết bị có thể đã biến mất */ }
        try { device.Dispose(); } catch { /* bỏ qua */ }
    }

    private void Reopen()
    {
        string description;
        string? error = null;
        lock (_gate)
        {
            if (_disposed) return;
            CloseDevice();
            try
            {
                OpenDevice();
                _retries = 0;
            }
            catch (Exception ex)
            {
                // Chưa có thiết bị dùng được (đang kết nối...): thử lại sau một lúc.
                _description = "Không có thiết bị phát";
                error = ex.Message;
                if (_retries++ < MaxRetries) _reopenTimer.Change(RetryMs, Timeout.Infinite);
            }
            description = _description;
        }

        DeviceChanged?.Invoke(description, error);
    }

    private void ScheduleReopen()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _retries = 0;
            _reopenTimer.Change(DebounceMs, Timeout.Infinite);
        }
    }

    // ---------- theo dõi thiết bị ----------

    private void StartWatching()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                // Chỉ quan tâm thiết bị phát (không phải micro).
                if (e.Flow == DataFlow.Render) ScheduleReopen();
            };
            _notifications.DeviceRemoved += (_, _) => ScheduleReopen();
            _notifications.DeviceStateChanged += (_, _) => ScheduleReopen();
        }
        catch
        {
            // Không theo dõi được thì vẫn phát bình thường trên thiết bị đã mở.
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Thiết bị bị rút/ngắt đột ngột làm luồng dừng kèm lỗi: mở lại trên thiết bị mặc định hiện tại.
        if (e.Exception is not null) ScheduleReopen();
    }

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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _reopenTimer.Dispose();
            CloseDevice();
        }

        try { _notifications?.Dispose(); } catch { /* bỏ qua */ }
        try { _enumerator?.Dispose(); } catch { /* bỏ qua */ }
    }
}
