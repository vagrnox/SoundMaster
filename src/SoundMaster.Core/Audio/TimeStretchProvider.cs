using NAudio.Wave;

namespace SoundMaster.Core.Audio;

/// <summary>
/// Đổi tốc độ phát mà giữ nguyên cao độ (WSOLA). Bọc quanh một nguồn stereo: ở tốc độ <c>s</c> nó
/// kéo từ nguồn <c>s</c> frame cho mỗi frame đầu ra, nên vị trí của nguồn (MixerEngine) chạy đúng theo tốc độ.
/// Tốc độ 1.0 đi thẳng, không xử lý. Dùng Monitor.TryEnter nên luồng âm thanh không bao giờ bị chặn.
/// </summary>
public sealed class TimeStretchProvider : ISampleProvider
{
    public const double MinSpeed = 0.1;
    public const double MaxSpeed = 3.0;

    private const int Hs = 512;          // bước tổng hợp (frame)
    private const int W = Hs * 2;        // độ dài cửa sổ, chồng lấn 50%
    private const int Tolerance = 256;   // vùng tìm điểm ghép tốt nhất (± frame)

    private readonly ISampleProvider _source;
    private readonly object _lock = new();
    private readonly float[] _window = new float[W];
    private readonly float[] _chunk = new float[4096 * 2];

    // Đệm đầu vào: interleaved stereo + bản mono để tính tương quan.
    private float[] _in = new float[8192 * 2];
    private float[] _mono = new float[8192];
    private long _inStart;               // chỉ số frame tuyệt đối của phần tử đầu đệm
    private int _inLen;                  // số frame đang có trong đệm

    // Đệm đầu ra (đã hoàn chỉnh) và vùng chồng lấn đang chờ cộng tiếp.
    private readonly float[] _tail = new float[Hs * 2];   // nửa sau của cửa sổ trước (Hs frame stereo)
    private readonly float[] _ready = new float[Hs * 2 * 4];
    private int _readyStart, _readyLen;  // theo frame

    private double _analysisPos;         // vị trí phân tích lý tưởng của cửa sổ kế tiếp
    private long _prevStart = -1;        // điểm bắt đầu thực tế của cửa sổ trước
    private bool _primed;

    private volatile float _speed = 1f;

    public TimeStretchProvider(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 2) throw new ArgumentException("Chỉ hỗ trợ stereo.", nameof(source));
        _source = source;
        for (int i = 0; i < W; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / W));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public double Speed
    {
        get => _speed;
        set => _speed = (float)Math.Clamp(value, MinSpeed, MaxSpeed);
    }

    /// <summary>Xóa mọi dữ liệu đang đệm (gọi sau khi tua / dừng / phát lại).</summary>
    public void Reset()
    {
        lock (_lock) ResetLocked();
    }

    private void ResetLocked()
    {
        _inStart = 0;
        _inLen = 0;
        _readyStart = _readyLen = 0;
        _analysisPos = 0;
        _prevStart = -1;
        _primed = false;
        Array.Clear(_tail);
    }

    public int Read(float[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public int Read(Span<float> buffer)
    {
        float speed = _speed;
        bool bypass = Math.Abs(speed - 1f) < 0.001f;

        if (!Monitor.TryEnter(_lock))
        {
            buffer.Clear();
            return buffer.Length;
        }

        try
        {
            if (bypass)
            {
                if (_primed || _inLen > 0) ResetLocked();
                return _source.Read(buffer);
            }

            int frames = buffer.Length / 2;
            int done = 0;
            while (done < frames)
            {
                if (_readyLen == 0) ProduceSegment(speed);
                int n = Math.Min(frames - done, _readyLen);
                _ready.AsSpan(_readyStart * 2, n * 2).CopyTo(buffer.Slice(done * 2, n * 2));
                _readyStart += n;
                _readyLen -= n;
                done += n;
            }
            return buffer.Length;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Tạo thêm Hs frame đầu ra hoàn chỉnh.</summary>
    private void ProduceSegment(float speed)
    {
        long start;
        if (!_primed)
        {
            start = 0;
            _analysisPos = 0;
        }
        else
        {
            _analysisPos += Hs * (double)speed;
            start = ChooseStart((long)Math.Round(_analysisPos));
        }

        EnsureInput(start + W);
        int rel = (int)(start - _inStart);

        // Cộng cửa sổ mới (có trọng số Hann) vào phần đuôi cửa sổ trước.
        _readyStart = 0;
        _readyLen = Hs;
        for (int i = 0; i < Hs; i++)
        {
            float w0 = _window[i];
            _ready[2 * i] = _tail[2 * i] + _in[2 * (rel + i)] * w0;
            _ready[2 * i + 1] = _tail[2 * i + 1] + _in[2 * (rel + i) + 1] * w0;
        }
        // Nửa sau của cửa sổ này trở thành đuôi chờ cộng ở lần sau.
        for (int i = 0; i < Hs; i++)
        {
            float w1 = _window[Hs + i];
            _tail[2 * i] = _in[2 * (rel + Hs + i)] * w1;
            _tail[2 * i + 1] = _in[2 * (rel + Hs + i) + 1] * w1;
        }

        _prevStart = start;
        _primed = true;
        Trim(start - Tolerance - W);
    }

    /// <summary>Chọn điểm bắt đầu quanh <paramref name="target"/> sao cho khớp nhất với phần tiếp nối tự nhiên của cửa sổ trước.</summary>
    private long ChooseStart(long target)
    {
        long refStart = _prevStart + Hs; // tiếp nối tự nhiên của cửa sổ trước
        EnsureInput(Math.Max(refStart + Hs, target + Tolerance + W));

        long lo = Math.Max(target - Tolerance, _inStart);
        long hi = target + Tolerance;
        if (lo > hi) return target;

        int refRel = (int)(refStart - _inStart);
        double best = double.NegativeInfinity;
        long bestStart = Math.Clamp(target, lo, hi);

        for (long s = lo; s <= hi; s++)
        {
            int rel = (int)(s - _inStart);
            double dot = 0, energy = 1e-9;
            for (int i = 0; i < Hs; i += 2) // bước 2 để giảm chi phí; đủ để khớp pha
            {
                float a = _mono[refRel + i], b = _mono[rel + i];
                dot += a * b;
                energy += b * b;
            }
            double score = dot / Math.Sqrt(energy);
            if (score > best) { best = score; bestStart = s; }
        }
        return bestStart;
    }

    /// <summary>Bảo đảm đệm chứa đến frame tuyệt đối <paramref name="endFrame"/> (loại trừ).</summary>
    private void EnsureInput(long endFrame)
    {
        long have = _inStart + _inLen;
        if (endFrame <= have) return;

        int need = (int)(endFrame - have);
        int capacityNeeded = _inLen + need;
        if (capacityNeeded > _mono.Length)
        {
            int cap = Math.Max(capacityNeeded, _mono.Length * 2);
            Array.Resize(ref _in, cap * 2);
            Array.Resize(ref _mono, cap);
        }

        // Đọc từ nguồn theo từng khối nhỏ; nguồn ngắn hơn thì phần còn lại là im lặng.
        var chunk = _chunk;
        int remaining = need;
        while (remaining > 0)
        {
            int n = Math.Min(remaining, chunk.Length / 2);
            int got = _source.Read(chunk.AsSpan(0, n * 2)) / 2;
            if (got < n) Array.Clear(chunk, got * 2, (n - got) * 2);
            for (int i = 0; i < n; i++)
            {
                int idx = _inLen + i;
                _in[2 * idx] = chunk[2 * i];
                _in[2 * idx + 1] = chunk[2 * i + 1];
                _mono[idx] = 0.5f * (chunk[2 * i] + chunk[2 * i + 1]);
            }
            _inLen += n;
            remaining -= n;
        }
    }

    /// <summary>Bỏ các frame trước <paramref name="keepFrom"/> (tuyệt đối) để đệm không phình ra.</summary>
    private void Trim(long keepFrom)
    {
        int drop = (int)Math.Min(keepFrom - _inStart, _inLen);
        if (drop < 4096) return;
        int remain = _inLen - drop;
        Array.Copy(_in, drop * 2, _in, 0, remain * 2);
        Array.Copy(_mono, drop, _mono, 0, remain);
        _inStart += drop;
        _inLen = remain;
    }
}
