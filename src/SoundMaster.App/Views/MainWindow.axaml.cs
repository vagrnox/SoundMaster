using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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

        _headerScroll.AddHandler(PointerPressedEvent, OnHeaderPressed, RoutingStrategies.Tunnel);
        _headerScroll.AddHandler(PointerMovedEvent, OnHeaderMoved, RoutingStrategies.Tunnel);
        _headerScroll.AddHandler(PointerReleasedEvent, OnHeaderReleased, RoutingStrategies.Tunnel);

        // Kéo file âm thanh thả vào bất kỳ đâu trong vùng làm việc để tạo track mới.
        DragDrop.SetAllowDrop(_workspace, true);
        _workspace.AddHandler(DragDrop.DragOverEvent, OnWorkspaceDragOver);
        _workspace.AddHandler(DragDrop.DropEvent, OnWorkspaceDrop);

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

    // ---------- kéo số thứ tự để đổi thứ tự track ----------

    /// <summary>Phải khớp chiều cao một hàng track (TrackViewModel.RowHeight).</summary>
    private const double TrackRowHeight = TrackViewModel.RowHeight;

    private int _dragIndex = -1;

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || !e.GetCurrentPoint(_headerScroll).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual src || !IsInGrip(src)) return;

        int index = RowIndexAt(e);
        if (index < 0 || index >= Vm.Tracks.Count || Vm.Tracks[index].IsLocked) return;

        _dragIndex = index;
        e.Pointer.Capture(_headerScroll);
        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragIndex < 0 || Vm is null) return;
        int target = Math.Clamp(RowIndexAt(e), 0, Vm.Tracks.Count - 1);
        // Đổi chỗ ngay khi con trỏ vượt sang hàng khác (sắp xếp trực tiếp).
        if (target != _dragIndex && Vm.MoveTrack(_dragIndex, target)) _dragIndex = target;
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        e.Pointer.Capture(null);
    }

    private int RowIndexAt(PointerEventArgs e)
        => (int)Math.Floor((e.GetPosition(_headerScroll).Y + _headerScroll.Offset.Y) / TrackRowHeight);

    private static bool IsInGrip(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
        {
            if (cur is Border b && b.Classes.Contains("grip")) return true;
        }
        return false;
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

    // ---------- kéo thả file để tạo track mới ----------

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnWorkspaceDragOver(object? sender, DragEventArgs e)
    {
        bool ok = Vm is { IsBusy: false } && HasFiles(e);
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnWorkspaceDrop(object? sender, DragEventArgs e)
    {
        if (Vm is null || Vm.IsBusy) return;
        var paths = (e.DataTransfer.TryGetFiles() ?? [])
            .Select(f => f.TryGetLocalPath())
            .OfType<string>()
            .Where(p => System.IO.File.Exists(p))
            .ToList();
        e.Handled = true;
        if (paths.Count > 0) await Vm.LoadFilesAsync(paths, replace: false);
    }

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
