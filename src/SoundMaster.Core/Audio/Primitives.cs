namespace SoundMaster.Core.Audio;

/// <summary>Định dạng cố định của engine: stereo float, 44.1 kHz.</summary>
public static class EngineFormat
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
}

public static class Decibels
{
    public static float ToLinear(float db, float floorDb = -60f)
        => db <= floorDb ? 0f : MathF.Pow(10f, db / 20f);

    public static float FromLinear(float linear)
        => linear <= 1e-6f ? -120f : 20f * MathF.Log10(linear);
}

/// <summary>
/// Luật pan kiểu "balance" cho stem stereo: phía không bị pan giữ nguyên 1.0,
/// phía bị pan giảm theo cos. Giữa = (1, 1).
/// </summary>
public static class PanLaw
{
    public static (float Left, float Right) Gains(float pan)
    {
        pan = Math.Clamp(pan, -1f, 1f);
        float left = pan <= 0f ? 1f : MathF.Cos(pan * MathF.PI / 2f);
        float right = pan >= 0f ? 1f : MathF.Cos(-pan * MathF.PI / 2f);
        return (left, right);
    }
}

/// <summary>
/// Đồng hồ đo đỉnh lock-free: audio thread ghi max, UI thread đọc và reset.
/// Giá trị luôn không âm nên thứ tự bit int trùng thứ tự float.
/// </summary>
public sealed class PeakMeter
{
    private int _left;
    private int _right;

    public void Update(float left, float right)
    {
        AtomicMax(ref _left, left);
        AtomicMax(ref _right, right);
    }

    public (float Left, float Right) ReadAndReset()
    {
        int l = Interlocked.Exchange(ref _left, 0);
        int r = Interlocked.Exchange(ref _right, 0);
        return (BitConverter.Int32BitsToSingle(l), BitConverter.Int32BitsToSingle(r));
    }

    private static void AtomicMax(ref int location, float value)
    {
        if (!(value >= 0f)) return; // loại NaN và số âm
        int newBits = BitConverter.SingleToInt32Bits(value);
        int current = Volatile.Read(ref location);
        while (newBits > current)
        {
            int previous = Interlocked.CompareExchange(ref location, newBits, current);
            if (previous == current) break;
            current = previous;
        }
    }
}

/// <summary>
/// Limiter đỉnh đơn giản (attack tức thời, release mượt) làm lưới an toàn cho master.
/// Đảm bảo đầu ra không vượt ceiling.
/// </summary>
public sealed class SafetyLimiter
{
    private readonly float _ceiling;
    private readonly float _releaseCoef;
    private float _gain = 1f;

    public SafetyLimiter(float ceilingDb = -0.3f, float releaseMs = 100f)
    {
        _ceiling = MathF.Pow(10f, ceilingDb / 20f);
        _releaseCoef = MathF.Exp(-1f / (EngineFormat.SampleRate * releaseMs / 1000f));
    }

    public bool Enabled { get; set; } = true;

    /// <summary>Mức giảm gain hiện tại (dB, ≤ 0).</summary>
    public float GainReductionDb => Decibels.FromLinear(_gain);

    public void Reset() => _gain = 1f;

    public void Process(float[] buffer, int frames)
    {
        if (!Enabled)
        {
            _gain = 1f;
            return;
        }

        for (int i = 0; i < frames; i++)
        {
            float l = buffer[2 * i];
            float r = buffer[2 * i + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));
            float target = peak > _ceiling ? _ceiling / peak : 1f;
            _gain = target < _gain ? target : target + (_gain - target) * _releaseCoef;
            buffer[2 * i] = l * _gain;
            buffer[2 * i + 1] = r * _gain;
        }
    }
}
