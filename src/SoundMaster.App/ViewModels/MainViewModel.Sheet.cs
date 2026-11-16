using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMaster.Core.Notation;

namespace SoundMaster.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>Đang ở view bản nhạc (nằm trên piano roll: Esc quay về piano roll).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRollVisible))]
    public partial bool IsSheet { get; set; }

    /// <summary>Piano roll đang hiện (không bị view bản nhạc che).</summary>
    public bool IsRollVisible => IsPianoRoll && !IsSheet;

    /// <summary>Bản nhạc dựng từ nốt của track đang chọn (dựng lại khi nốt, BPM hoặc lời đổi).</summary>
    [ObservableProperty]
    public partial Score? SheetScore { get; set; }

    /// <summary>Ví dụ "12 âm tiết / 30 nốt" cho ô nhập lời.</summary>
    [ObservableProperty]
    public partial string LyricsInfo { get; set; } = "";

    partial void OnIsSheetChanged(bool value)
    {
        if (value) RebuildScore();
    }

    /// <summary>Dựng lại bản nhạc từ nốt, BPM và lời của track đang chọn.</summary>
    private void RebuildScore()
    {
        if (!IsSheet) return;
        if (SelectedTrack is not { } track)
        {
            SheetScore = null;
            LyricsInfo = "";
            return;
        }

        var score = ScoreBuilder.Build(track.Notes, Timeline.Bpm);
        var syllables = Lyrics.Parse(track.Lyrics);
        int assigned = score.AssignLyrics(syllables);
        LyricsInfo = syllables.Count == 0
            ? $"{score.Slots.Count} nốt chưa có lời"
            : $"{syllables.Count} âm tiết / {score.Slots.Count} nốt" + (syllables.Count > score.Slots.Count ? " — thừa lời" : "");
        _ = assigned;
        SheetScore = score;
    }

    /// <summary>Mở bản nhạc cho một track (tự bóc nốt nếu cần).</summary>
    public async Task OpenSheetAsync(TrackViewModel track)
    {
        await OpenPianoRollAsync(track);
        IsSheet = true;
        RebuildScore();
    }

    [RelayCommand]
    private void ShowSheet()
    {
        IsSheet = true;
        RebuildScore();
    }

    [RelayCommand]
    private void CloseSheet() => IsSheet = false;

    [RelayCommand]
    private void SheetPreviousTrack() => StepTrack(-1);

    [RelayCommand]
    private void SheetNextTrack() => StepTrack(1);

    private void StepTrack(int delta)
    {
        if (Tracks.Count == 0) return;
        int i = SelectedTrack is { } t ? Tracks.IndexOf(t) : 0;
        Tracks[(i + delta + Tracks.Count) % Tracks.Count].RequestSelect();
    }

    /// <summary>Xuất bản nhạc đang xem ra MusicXML (kèm lời).</summary>
    public void ExportMusicXml(string path)
    {
        if (SheetScore is not { } score || SelectedTrack is not { } track) return;
        try
        {
            MusicXmlExporter.Export(path, score, track.Name);
            Status = $"Đã xuất bản nhạc \"{track.Name}\" ra {System.IO.Path.GetFileName(path)} (MusicXML, mở bằng MuseScore).";
        }
        catch (Exception ex)
        {
            Status = $"Không xuất được MusicXML: {ex.Message}";
        }
    }
}
