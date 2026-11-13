using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SoundMaster.App.ViewModels;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.Views;

public partial class MainWindow : Window
{
    private readonly ComboBox _formatBox;

    public MainWindow()
    {
        InitializeComponent();
        _formatBox = this.FindControl<ComboBox>("FormatBox")!;
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Chọn thư mục chứa các stem",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await Vm.OpenFolderAsync(path);
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn file stem",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.mp3", "*.aiff", "*.aif", "*.m4a", "*.wma"] },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await Vm.LoadFilesAsync(paths, replace: false);
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất bản mix",
            SuggestedFileName = "mix.wav",
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var format = _formatBox.SelectedIndex switch
        {
            0 => ExportFormat.Pcm16,
            2 => ExportFormat.Float32,
            _ => ExportFormat.Pcm24,
        };
        await Vm.ExportAsync(path, format);
    }

    private void OnMasterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not null) Vm.MasterGainDb = 0;
    }
}
