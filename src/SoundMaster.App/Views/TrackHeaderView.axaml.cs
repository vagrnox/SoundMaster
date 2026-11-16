using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SoundMaster.App.ViewModels;

namespace SoundMaster.App.Views;

public partial class TrackHeaderView : UserControl
{
    public TrackHeaderView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private TrackViewModel? Track => DataContext as TrackViewModel;
    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>Bấm vào header (ngoài các nút) cũng chọn track.</summary>
    private void OnHeaderPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => Track?.RequestSelect();

    private async void OnTranscribe(object? sender, RoutedEventArgs e)
    {
        if (Main is { } main && Track is { } track) await main.TranscribeTrackAsync(track);
    }

    private void OnToggleNotes(object? sender, RoutedEventArgs e)
    {
        if (Track is { } track) track.ShowNotes = !track.ShowNotes;
    }

    private async void OnExportMidi(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } main || Track is not { HasNotes: true } track) return;
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
        if (path is not null) main.ExportMidi(track, path);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Main?.MoveTrackUpCommand.Execute(Track);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => Main?.MoveTrackDownCommand.Execute(Track);
    private void OnRemove(object? sender, RoutedEventArgs e) => Main?.RemoveTrackCommand.Execute(Track);
}
