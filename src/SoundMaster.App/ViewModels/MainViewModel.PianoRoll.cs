using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Transcription;

namespace SoundMaster.App.ViewModels;

public partial class MainViewModel
{
    public const double MinRollRowHeight = 14;
    public const double MaxRollRowHeight = 30;
    private const double MinNoteSeconds = 0.03;

    private static readonly string[] MidiExtensions = [".mid", ".midi"];

    public static bool IsMidiFile(string path)
        => MidiExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Đang ở view piano roll (thay cho vùng mixer).</summary>
    [ObservableProperty]
    public partial bool IsPianoRoll { get; set; }

    /// <summary>Số nốt MIDI (có thể lẻ) ở mép trên của vùng nhìn thấy trong piano roll.</summary>
    [ObservableProperty]
    public partial double RollTopNote { get; set; } = 84;

    /// <summary>Chiều cao một hàng nửa cung (px).</summary>
    [ObservableProperty]
    public partial double RollRowHeight { get; set; } = 20;

    /// <summary>Nốt được chọn trong piano roll (null nếu không có).</summary>
    [ObservableProperty]
    public partial TranscribedNote? SelectedNote { get; set; }

    /// <summary>Ở piano roll vẫn nghe cả stem gốc (mặc định chỉ nghe nốt).</summary>
    [ObservableProperty]
    public partial bool RollListenStems { get; set; }

    /// <summary>Kéo / vẽ nốt bám theo lưới nhịp.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollSnapSeconds))]
    public partial bool SnapToGrid { get; set; } = true;

    public static IReadOnlyList<string> GridChoices { get; } = ["1/4", "1/8", "1/16", "1/32"];
    private static readonly int[] GridDivisions = [4, 8, 16, 32];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollSnapSeconds), nameof(GridSeconds))]
    public partial int GridIndex { get; set; } = 2;

    /// <summary>Độ dài một ô lưới (giây) theo BPM và độ chia.</summary>
    public double GridSeconds => 60.0 / Math.Max(1, Timeline.Bpm) * 4.0 / GridDivisions[Math.Clamp(GridIndex, 0, GridDivisions.Length - 1)];

    /// <summary>Bước bám lưới khi kéo (0 = tắt).</summary>
    public double RollSnapSeconds => SnapToGrid ? GridSeconds : 0;

    partial void OnIsPianoRollChanged(bool value) => RefreshNotePlayback();

    partial void OnRollListenStemsChanged(bool value) => RefreshNotePlayback();

    private void OnTimelineChangedForRoll(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.Bpm))
            OnPropertyChanged(nameof(RollSnapSeconds));
    }

    /// <summary>
    /// Đồng bộ đàn tổng hợp với trạng thái: ở piano roll chỉ phát nốt của track đang chọn (và tắt tiếng stem
    /// trừ khi bật "nghe stem"); ở mixer chỉ phát các track MIDI.
    /// </summary>
    private void RefreshNotePlayback()
    {
        if (IsPianoRoll)
        {
            var sel = SelectedTrack;
            _engine.Notes.Set(sel is null ? [] : [sel.Notes]);
            _engine.StemsMuted = !RollListenStems;
        }
        else
        {
            _engine.Notes.Set(Tracks.Where(t => t.IsMidi).Select(t => t.Notes));
            _engine.StemsMuted = false;
        }
    }

    // ---------- mở / đóng ----------

    /// <summary>Mở piano roll cho một track: chọn track, nếu chưa bóc nốt thì bóc ngay rồi hiện.</summary>
    public async Task OpenPianoRollAsync(TrackViewModel track)
    {
        track.RequestSelect();
        IsPianoRoll = true;
        SelectedNote = null;
        if (!track.HasNotes && !track.IsMidi) await TranscribeTrackAsync(track);
    }

    [RelayCommand]
    private void ClosePianoRoll()
    {
        PreviewNoteOff();
        IsPianoRoll = false;
    }

    /// <summary>Bóc lại nốt của track đang chọn (mất các chỉnh sửa tay).</summary>
    [RelayCommand]
    private async Task RetranscribeAsync()
    {
        if (SelectedTrack is not { } track) return;
        if (track.IsMidi)
        {
            Status = "Track MIDI không có audio để bóc lại.";
            return;
        }
        SelectedNote = null;
        await TranscribeTrackAsync(track);
    }

    public void PreviewNoteOn(int midiNote) => _engine.Preview.NoteOn(midiNote);

    public void PreviewNoteOff() => _engine.Preview.NoteOff();

    // ---------- nhập MIDI ----------

    /// <summary>Nhập file MIDI: mỗi track/kênh có nốt thành một track MIDI, mở piano roll và phát luôn.</summary>
    public async Task ImportMidiAsync(string path, bool play = true)
    {
        if (IsBusy) return;
        IsBusy = true;
        MidiImport import;
        try
        {
            Status = $"Đang đọc {Path.GetFileName(path)}…";
            import = await Task.Run(() => MidiImporter.Import(path));
        }
        catch (Exception ex)
        {
            Status = $"Không đọc được MIDI: {ex.Message}";
            IsBusy = false;
            return;
        }

        try
        {
            var lanes = import.Lanes.Where(l => l.Notes.Count > 0).ToList();
            if (lanes.Count == 0)
            {
                Status = "File MIDI không có nốt nào (kênh trống bị bỏ qua).";
                return;
            }

            bool fresh = Tracks.Count == 0;
            _engine.Pause();
            IsPlaying = false;

            TrackViewModel? first = null;
            foreach (var lane in lanes)
            {
                var audio = AudioTrack.Silent(lane.Name, lane.Notes.Max(n => n.EndSeconds) + 0.5);
                var track = AddTrackViewModel(audio);
                track.IsMidi = true;
                track.Notes = lane.Notes;
                first ??= track;
            }

            if (fresh) Timeline.Bpm = Math.Round(import.Bpm, 2);
            _autoBpmPending = false;
            AfterTracksChanged(resetTransport: fresh);
            Status = $"Đã nhập {lanes.Count} track MIDI ({lanes.Sum(l => l.Notes.Count)} nốt) từ {Path.GetFileName(path)}.";

            await OpenPianoRollAsync(first!);
            if (play && !IsPlaying) PlayPauseCommand.Execute(null);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- chỉnh sửa nốt ----------

    private static TranscribedNote Confirmed(TranscribedNote n) => n.Confidence >= 1 ? n : n with { Confidence = 1 };

    /// <summary>Thay một nốt bằng bản đã sửa (sửa tay thì nốt thành "chính").</summary>
    public void ReplaceNote(TranscribedNote old, TranscribedNote updated)
    {
        if (SelectedTrack is not { } track) return;
        updated = Confirmed(updated);
        track.Notes = track.Notes.Select(n => ReferenceEquals(n, old) ? updated : n).ToList();
        if (ReferenceEquals(SelectedNote, old)) SelectedNote = updated;
    }

    /// <summary>Vẽ nốt mới (độ dài một ô lưới) tại thời điểm và cao độ cho trước.</summary>
    public void AddNote(double seconds, int midi)
    {
        if (SelectedTrack is not { } track) return;
        double length = Math.Max(GridSeconds, 0.1);
        double start = RollSnapSeconds > 0 ? Math.Floor(seconds / RollSnapSeconds) * RollSnapSeconds : seconds;
        var note = new TranscribedNote(Math.Max(0, start), Math.Max(0, start) + length, Math.Clamp(midi, 0, 127), 96);
        track.Notes = track.Notes.Append(note).OrderBy(n => n.StartSeconds).ToList();
        SelectedNote = note;
    }

    /// <summary>Xóa một nốt khỏi track.</summary>
    public void DeleteNote(TrackViewModel track, TranscribedNote note)
    {
        track.Notes = track.Notes.Where(n => !ReferenceEquals(n, note)).ToList();
        if (ReferenceEquals(SelectedNote, note)) SelectedNote = null;
    }

    /// <summary>Xóa nốt đang chọn.</summary>
    public void DeleteSelectedNote()
    {
        if (SelectedTrack is { } track && SelectedNote is { } note) DeleteNote(track, note);
    }

    /// <summary>Xóa tất cả nốt nghi ngờ (màu đỏ) của track đang chọn.</summary>
    [RelayCommand]
    private void DeleteDoubtfulNotes()
    {
        if (SelectedTrack is not { } track) return;
        int before = track.Notes.Count;
        track.Notes = track.Notes.Where(n => !n.IsDoubtful).ToList();
        SelectedNote = null;
        Status = $"Đã xóa {before - track.Notes.Count} nốt nghi ngờ khỏi \"{track.Name}\".";
    }

    /// <summary>Tách nốt đang chọn tại playhead (nếu playhead nằm trong nốt), không thì tách đôi ở giữa.</summary>
    [RelayCommand]
    private void SplitNote()
    {
        if (SelectedTrack is not { } track || SelectedNote is not { } note)
        {
            Status = "Chọn một nốt để tách.";
            return;
        }

        double at = Timeline.PositionSeconds;
        if (RollSnapSeconds > 0) at = Math.Round(at / RollSnapSeconds) * RollSnapSeconds;
        if (at <= note.StartSeconds + MinNoteSeconds || at >= note.EndSeconds - MinNoteSeconds)
            at = (note.StartSeconds + note.EndSeconds) / 2;
        if (note.Duration < 2 * MinNoteSeconds) return;

        var a = Confirmed(note with { EndSeconds = at });
        var b = Confirmed(note with { StartSeconds = at });
        track.Notes = track.Notes.Where(n => !ReferenceEquals(n, note)).Append(a).Append(b)
            .OrderBy(n => n.StartSeconds).ToList();
        SelectedNote = a;
    }

    /// <summary>Gộp nốt đang chọn với nốt kế tiếp (theo thời gian): thành một nốt dài, giữ cao độ của nốt chọn.</summary>
    [RelayCommand]
    private void MergeNote()
    {
        if (SelectedTrack is not { } track || SelectedNote is not { } note)
        {
            Status = "Chọn một nốt để gộp với nốt kế tiếp.";
            return;
        }

        var next = track.Notes
            .Where(n => !ReferenceEquals(n, note) && n.StartSeconds >= note.StartSeconds)
            .OrderBy(n => n.StartSeconds).ThenBy(n => Math.Abs(n.MidiNote - note.MidiNote))
            .FirstOrDefault();
        if (next is null)
        {
            Status = "Không có nốt nào phía sau để gộp.";
            return;
        }

        var merged = new TranscribedNote(
            Math.Min(note.StartSeconds, next.StartSeconds), Math.Max(note.EndSeconds, next.EndSeconds),
            note.MidiNote, Math.Max(note.Velocity, next.Velocity));
        track.Notes = track.Notes.Where(n => !ReferenceEquals(n, note) && !ReferenceEquals(n, next)).Append(merged)
            .OrderBy(n => n.StartSeconds).ToList();
        SelectedNote = merged;
    }

    /// <summary>Căn đầu và cuối mọi nốt của track đang chọn vào lưới nhịp (theo BPM và độ chia đang chọn).</summary>
    [RelayCommand]
    private void QuantizeNotes()
    {
        if (SelectedTrack is not { } track || track.Notes.Count == 0) return;
        double g = GridSeconds;
        if (g <= 0) return;

        var snapped = track.Notes.Select(n =>
        {
            double s = Math.Round(n.StartSeconds / g) * g;
            double e = Math.Round(n.EndSeconds / g) * g;
            if (e <= s) e = s + g;
            return n with { StartSeconds = s, EndSeconds = e };
        }).OrderBy(n => n.StartSeconds).ToList();

        // Hai nốt cùng cao độ chồng nhau sau khi căn: cắt nốt trước tại đầu nốt sau (trùng hẳn thì bỏ nốt thừa).
        var result = new List<TranscribedNote>();
        foreach (var n in snapped)
        {
            int prev = result.FindLastIndex(r => r.MidiNote == n.MidiNote && r.EndSeconds > n.StartSeconds);
            if (prev >= 0)
            {
                var p = result[prev];
                if (n.StartSeconds - p.StartSeconds < 1e-6) { result[prev] = p with { EndSeconds = Math.Max(p.EndSeconds, n.EndSeconds) }; continue; }
                result[prev] = p with { EndSeconds = n.StartSeconds };
            }
            result.Add(n);
        }

        track.Notes = result;
        SelectedNote = null;
        Status = $"Đã căn {result.Count} nốt vào lưới {GridChoices[Math.Clamp(GridIndex, 0, GridChoices.Count - 1)]} ({Timeline.BpmText} BPM).";
    }
}
