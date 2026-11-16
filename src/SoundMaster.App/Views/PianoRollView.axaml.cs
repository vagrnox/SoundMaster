using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SoundMaster.App.Controls;
using SoundMaster.App.ViewModels;

namespace SoundMaster.App.Views;

public partial class PianoRollView : UserControl
{
    private readonly PianoRollCanvas _roll;
    private readonly PianoKeysView _keys;
    private readonly TimelineRuler _ruler;
    private MainViewModel? _vm;
    private TrackViewModel? _watchedTrack;
    private bool _pendingFit = true;

    public PianoRollView()
    {
        InitializeComponent();
        _roll = this.FindControl<PianoRollCanvas>("Roll")!;
        _keys = this.FindControl<PianoKeysView>("Keys")!;
        _ruler = this.FindControl<TimelineRuler>("RollRuler")!;

        _keys.NotePressed += n => _vm?.PreviewNoteOn(n);
        _keys.NoteReleased += () => _vm?.PreviewNoteOff();
        _roll.NotePressed += n => _vm?.PreviewNoteOn(n);
        _roll.NoteReleased += () => _vm?.PreviewNoteOff();
        _roll.NoteChanged += (old, updated) => _vm?.ReplaceNote(old, updated);
        _roll.NoteAddRequested += (seconds, midi) => _vm?.AddNote(seconds, midi);
        _roll.NoteDeleteRequested += note =>
        {
            if (_vm?.SelectedTrack is { } track) _vm.DeleteNote(track, note);
        };

        // Chiều rộng vùng lưới quyết định số giây nhìn thấy (dùng chung với mixer).
        _ruler.SizeChanged += (_, e) =>
        {
            if (_vm is not null && e.NewSize.Width > 0) _vm.Timeline.ViewportWidth = e.NewSize.Width;
        };
        _roll.SizeChanged += (_, _) => TryFitPitch();

        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void AttachViewModel()
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as MainViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        WatchTrack(_vm?.SelectedTrack);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPianoRoll) && _vm!.IsPianoRoll)
        {
            _pendingFit = true;
            TryFitPitch();
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedTrack))
        {
            _pendingFit = true;
            WatchTrack(_vm!.SelectedTrack);
            TryFitPitch();
        }
    }

    private void WatchTrack(TrackViewModel? track)
    {
        if (ReferenceEquals(track, _watchedTrack)) return;
        if (_watchedTrack is not null) _watchedTrack.PropertyChanged -= OnTrackChanged;
        _watchedTrack = track;
        if (_watchedTrack is not null) _watchedTrack.PropertyChanged += OnTrackChanged;
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Bóc xong (Notes từ rỗng thành có nốt) thì căn lại khoảng cao độ cho vừa các nốt.
        if (e.PropertyName == nameof(TrackViewModel.Notes) && _pendingFit) TryFitPitch();
    }

    /// <summary>Căn khung nhìn theo khoảng cao độ của các nốt (chờ tới khi có nốt và lưới đã có kích thước).</summary>
    private void TryFitPitch()
    {
        if (_vm is null || !_vm.IsPianoRoll || !_pendingFit) return;
        double h = _roll.Bounds.Height;
        if (h <= 0) return;

        var notes = _vm.SelectedTrack?.Notes;
        int lo = 57, hi = 64; // không có nốt: quanh C4
        if (notes is { Count: > 0 })
        {
            lo = notes.Min(n => n.MidiNote);
            hi = notes.Max(n => n.MidiNote);
        }
        else if (_vm.SelectedTrack?.IsTranscribing != false)
        {
            // Đang bóc hoặc chưa chọn track: chờ có nốt rồi mới căn.
            _vm.RollRowHeight = 18;
            _vm.RollTopNote = 66;
            return;
        }

        int span = hi - lo + 1 + 4; // chừa 2 hàng mỗi đầu
        double rh = Math.Floor(Math.Clamp(h / span, MainViewModel.MinRollRowHeight, MainViewModel.MaxRollRowHeight) * 2) / 2;
        double visible = h / rh;
        _vm.RollRowHeight = rh;
        _vm.RollTopNote = ClampTop((lo + hi + 1) / 2.0 + visible / 2, visible);
        _pendingFit = false;
    }

    private static double ClampTop(double top, double visibleRows) => Math.Clamp(top, Math.Min(visibleRows, 128), 128);

    /// <summary>Ctrl+lăn: zoom thời gian. Shift/lăn ngang: cuộn thời gian. Alt+lăn: zoom hàng. Lăn thường: cuộn cao độ.</summary>
    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_vm is null) return;
        double x = e.GetPosition(_ruler).X;
        double h = Math.Max(1, _roll.Bounds.Height);

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (x < 0) return;
            _vm.Timeline.ZoomAround(e.Delta.Y > 0 ? 1.2 : 1 / 1.2, x);
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || Math.Abs(e.Delta.X) > 0.001)
        {
            double notches = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? e.Delta.Y : e.Delta.X;
            _vm.Timeline.OffsetSeconds -= notches * 60 / _vm.Timeline.PixelsPerSecond;
        }
        else if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            double rh = Math.Clamp(_vm.RollRowHeight * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15),
                MainViewModel.MinRollRowHeight, MainViewModel.MaxRollRowHeight);
            _vm.RollRowHeight = rh;
            _vm.RollTopNote = ClampTop(_vm.RollTopNote, h / rh);
        }
        else
        {
            _vm.RollTopNote = ClampTop(_vm.RollTopNote + e.Delta.Y * 3, h / _vm.RollRowHeight);
        }

        e.Handled = true;
    }

    private void OnTrackPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TrackViewModel track) track.RequestSelect();
    }

    private async void OnExportMidi(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || _vm.SelectedTrack is not { HasNotes: true } track) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất MIDI",
            SuggestedFileName = track.Name + ".mid",
            DefaultExtension = "mid",
            FileTypeChoices = [new FilePickerFileType("MIDI") { Patterns = ["*.mid"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is not null) _vm.ExportMidi(track, path);
    }
}
