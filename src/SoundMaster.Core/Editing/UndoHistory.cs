namespace SoundMaster.Core.Editing;

/// <summary>
/// Lịch sử undo/redo dựa trên ảnh chụp trạng thái. Người dùng chỉnh liên tục (kéo volume, gõ tên) nên
/// các thay đổi được gộp: chỉ khi trạng thái đã "yên" đủ lâu mới thành một bước. Mỗi trạng thái có một
/// <c>key</c> (chuỗi) để so sánh nhanh; trạng thái T chỉ được giữ làm điểm quay lại.
/// </summary>
public sealed class UndoHistory<T>
{
    /// <summary>Số bước tối đa được giữ (mỗi bước giữ tham chiếu tới các stem trong RAM).</summary>
    public const int MaxDepth = 100;

    private readonly TimeSpan _quiet;
    private readonly List<(T State, string Key)> _undo = new();
    private readonly Stack<(T State, string Key)> _redo = new();

    private (T State, string Key) _baseline;
    private string? _pendingKey;
    private TimeSpan _pendingSince;

    public UndoHistory(T initial, string key, TimeSpan quietPeriod)
    {
        _quiet = quietPeriod;
        _baseline = (initial, key);
    }

    public string BaselineKey => _baseline.Key;

    /// <summary>Có thay đổi chưa chốt thành bước (đang chỉnh dở).</summary>
    public bool HasPending(string currentKey) => currentKey != _baseline.Key;

    public bool CanUndo(string currentKey) => _undo.Count > 0 || HasPending(currentKey);
    public bool CanRedo(string currentKey) => _redo.Count > 0 && !HasPending(currentKey);

    /// <summary>Bỏ toàn bộ lịch sử và lấy trạng thái này làm mốc (sau khi mở project).</summary>
    public void Reset(T state, string key)
    {
        _undo.Clear();
        _redo.Clear();
        _baseline = (state, key);
        _pendingKey = null;
    }

    /// <summary>Chấp nhận trạng thái hiện tại làm mốc mà không tạo bước (sau khi khôi phục từ undo/redo).</summary>
    public void Rebase(T state, string key)
    {
        _baseline = (state, key);
        _pendingKey = null;
    }

    /// <summary>Gọi định kỳ với trạng thái hiện tại; chốt bước khi trạng thái đã yên đủ lâu.</summary>
    public void Observe(T current, string key, TimeSpan now)
    {
        if (key == _baseline.Key)
        {
            _pendingKey = null;
            return;
        }

        if (key != _pendingKey)
        {
            _pendingKey = key;
            _pendingSince = now;
            return;
        }

        if (now - _pendingSince >= _quiet) Commit(current, key);
    }

    /// <summary>Chốt ngay thay đổi đang chờ (trước khi undo/redo hoặc lưu).</summary>
    public void Flush(T current, string key)
    {
        if (key != _baseline.Key) Commit(current, key);
    }

    private void Commit(T current, string key)
    {
        PushUndo(_baseline);
        _redo.Clear();
        _baseline = (current, key);
        _pendingKey = null;
    }

    private void PushUndo((T State, string Key) item)
    {
        _undo.Add(item);
        if (_undo.Count > MaxDepth) _undo.RemoveAt(0);
    }

    private (T State, string Key) PopUndo()
    {
        var item = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return item;
    }

    public bool TryUndo(T current, string key, out T state)
    {
        Flush(current, key);
        if (_undo.Count == 0)
        {
            state = default!;
            return false;
        }

        _redo.Push(_baseline);
        _baseline = PopUndo();
        _pendingKey = null;
        state = _baseline.State;
        return true;
    }

    public bool TryRedo(T current, string key, out T state)
    {
        Flush(current, key);
        if (_redo.Count == 0)
        {
            state = default!;
            return false;
        }

        PushUndo(_baseline);
        _baseline = _redo.Pop();
        _pendingKey = null;
        state = _baseline.State;
        return true;
    }
}
