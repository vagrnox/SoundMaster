using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using SoundMaster.App.Controls;
using SoundMaster.App.ViewModels;
using SoundMaster.Core.Audio;

namespace SoundMaster.App.Views;

public partial class MainWindow : Window
{
    private readonly Grid _workspace;
    private readonly TimelineRuler _barRuler;
    private readonly ScrollViewer _headerScroll;
    private readonly ScrollViewer _laneScroll;
    private bool _syncingScroll;

    public MainWindow()
    {
        InitializeComponent();

        _workspace = this.FindControl<Grid>("Workspace")!;
        _barRuler = this.FindControl<TimelineRuler>("BarRuler")!;
        _headerScroll = this.FindControl<ScrollViewer>("HeaderScroll")!;
        _laneScroll = this.FindControl<ScrollViewer>("LaneScroll")!;

        // Chiều rộng vùng lane quyết định số giây nhìn thấy (cho zoom-to-fit và thanh cuộn).
        _barRuler.SizeChanged += (_, e) =>
        {
            if (Vm is null) return;
            bool first = Vm.Timeline.ViewportWidth <= 0;
            Vm.Timeline.ViewportWidth = e.NewSize.Width;
            if (first && Vm.Tracks.Count > 0) Vm.Timeline.ZoomToFit();
        };

        // Sidebar và lane cuộn dọc đồng bộ với nhau.
        _headerScroll.ScrollChanged += (_, _) => SyncVerticalScroll(_headerScroll, _laneScroll);
        _laneScroll.ScrollChanged += (_, _) => SyncVerticalScroll(_laneScroll, _headerScroll);

        _workspace.AddHandler(PointerWheelChangedEvent, OnWorkspaceWheel, RoutingStrategies.Tunnel);

        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void SyncVerticalScroll(ScrollViewer from, ScrollViewer to)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            to.Offset = new Vector(to.Offset.X, from.Offset.Y);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>Ctrl+lăn: zoom quanh con trỏ. Shift+lăn hoặc lăn ngang: cuộn timeline. Lăn thường: cuộn dọc.</summary>
    private void OnWorkspaceWheel(object? sender, PointerWheelEventArgs e)
    {
        var timeline = Vm?.Timeline;
        if (timeline is null) return;

        double x = e.GetPosition(_barRuler).X;
        if (x < 0) return; // đang ở sidebar

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            timeline.ZoomAround(e.Delta.Y > 0 ? 1.2 : 1 / 1.2, x);
            e.Handled = true;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0 || Math.Abs(e.Delta.X) > 0.001)
        {
            double notches = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? e.Delta.Y : e.Delta.X;
            timeline.OffsetSeconds -= notches * 60 / timeline.PixelsPerSecond;
            e.Handled = true;
        }
    }

    // ---------- nạp stem ----------

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
            Title = "Chọn file âm thanh để thêm thành track",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio") { Patterns = ["*.wav", "*.mp3", "*.aiff", "*.aif", "*.m4a", "*.wma"] },
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await Vm.LoadFilesAsync(paths, replace: false);
    }

    private void OnLoadDemo(object? sender, RoutedEventArgs e) => Vm?.LoadDemoCommand.Execute(null);
    private void OnClear(object? sender, RoutedEventArgs e) => Vm?.ClearCommand.Execute(null);

    // ---------- xuất file ----------

    private void OnExport16(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Pcm16);
    private void OnExport24(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Pcm24);
    private void OnExport32(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Float32);

    private async System.Threading.Tasks.Task ExportAsync(ExportFormat format)
    {
        if (Vm is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Xuất bản mix",
            SuggestedFileName = Vm.SongFileName + ".wav",
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV") { Patterns = ["*.wav"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        await Vm.ExportAsync(path, format);
    }
}
