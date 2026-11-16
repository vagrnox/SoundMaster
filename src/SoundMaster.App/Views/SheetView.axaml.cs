using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SoundMaster.App.Controls;
using SoundMaster.App.ViewModels;

namespace SoundMaster.App.Views;

public partial class SheetView : UserControl
{
    public SheetView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnExportMusicXml(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedTrack: { } track } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất MusicXML",
            SuggestedFileName = track.Name + ".musicxml",
            DefaultExtension = "musicxml",
            FileTypeChoices = [new FilePickerFileType("MusicXML") { Patterns = ["*.musicxml", "*.xml"] }],
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportMusicXml(path);
    }

    private async void OnExportMidi(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedTrack: { HasNotes: true } track } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất MIDI",
            SuggestedFileName = track.Name + ".mid",
            DefaultExtension = "mid",
            FileTypeChoices = [new FilePickerFileType("MIDI") { Patterns = ["*.mid"] }],
        });
        if (file?.TryGetLocalPath() is { } path) vm.ExportMidi(track, path);
    }

    private async void OnSavePng(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedTrack: { } track } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Lưu bản nhạc thành ảnh",
            SuggestedFileName = track.Name + ".png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            SavePng(path);
            vm.Status = $"Đã lưu ảnh bản nhạc {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            vm.Status = $"Không lưu được ảnh: {ex.Message}";
        }
    }

    /// <summary>Vẽ bản nhạc ra ảnh PNG (chiều rộng 1100 px, độ nét gấp đôi).</summary>
    public void SavePng(string path)
    {
        var sheet = this.FindControl<SheetMusicView>("Sheet")!;
        const double width = 1100;
        var size = sheet.DesiredSizeFor(width);

        // Dựng một bản riêng ở kích thước cố định để ảnh không phụ thuộc cửa sổ.
        var copy = new SheetMusicView { Score = sheet.Score, Title = sheet.Title, PositionSeconds = -1 };
        copy.Measure(size);
        copy.Arrange(new Rect(size));

        var pixels = new PixelSize((int)(size.Width * 2), (int)Math.Ceiling(size.Height * 2));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(192, 192));
        bitmap.Render(copy);
        bitmap.Save(path);
    }
}
