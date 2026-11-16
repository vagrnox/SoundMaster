using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
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
    private readonly ItemsControl _headerItems;
    private readonly ItemsControl _laneItems;
    private readonly Canvas _dragLayer;
    private bool _syncingScroll;

    public MainWindow()
    {
        InitializeComponent();

        _workspace = this.FindControl<Grid>("Workspace")!;
        _barRuler = this.FindControl<TimelineRuler>("BarRuler")!;
        _headerScroll = this.FindControl<ScrollViewer>("HeaderScroll")!;
        _laneScroll = this.FindControl<ScrollViewer>("LaneScroll")!;
        _headerItems = this.FindControl<ItemsControl>("HeaderItems")!;
        _laneItems = this.FindControl<ItemsControl>("LaneItems")!;
        _dragLayer = this.FindControl<Canvas>("DragLayer")!;

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
        _headerScroll.PointerCaptureLost += OnHeaderCaptureLost;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Bấm đúp vào header của một track (không phải waveform, vì ở đó bấm-rồi-kéo liền nhau là tạo vùng): bóc nốt (nếu cần) rồi mở piano roll.
        _headerScroll.AddHandler(DoubleTappedEvent, OnTrackDoubleTapped);

        // Kéo file âm thanh thả vào bất kỳ đâu trong vùng làm việc để tạo track mới.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWorkspaceDragOver);
        AddHandler(DragDrop.DropEvent, OnWorkspaceDrop);

        Closing += OnWindowClosing;
        Opened += async (_, _) => await CheckRecoveryAsync();
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Visual source) return;

        // Bỏ qua khi bấm đúp vào ô tên (đổi tên) hoặc các nút / thanh điều khiển.
        foreach (var v in source.GetSelfAndVisualAncestors())
        {
            if (v is TextBox or Button or Avalonia.Controls.Primitives.ToggleButton or VolumeBar or PanBar) return;
            if (v is TrackHeaderView or ScrollViewer) break;
        }

        if ((source as StyledElement)?.DataContext is TrackViewModel track)
        {

            e.Handled = true;
            await vm.OpenPianoRollAsync(track);
        }
    }

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
    //
    // Bấm vào số thứ tự: hàng đó thành ô trống, một "ghost" (ảnh chụp header) bám theo con trỏ.
    // Qua nửa hàng khác thì hàng đó trượt lên/xuống nhường chỗ. Thả = chuyển; Esc = hủy, mọi thứ về chỗ cũ.

    /// <summary>Phải khớp chiều cao một hàng track (TrackViewModel.RowHeight).</summary>
    private const double TrackRowHeight = TrackViewModel.RowHeight;
    private const double DragThreshold = 4;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(140);

    private int _dragFrom = -1;        // chỉ số gốc của track đang kéo
    private int _dragTarget = -1;      // chỗ sẽ thả
    private bool _dragActive;          // đã vượt ngưỡng, ghost đang hiện
    private bool _dragFinishing;       // đang chạy hoạt ảnh kết thúc
    private double _pressY;            // vị trí bấm (trong viewport), để tính ngưỡng
    private double _grabOffset;        // con trỏ cách mép trên hàng bao nhiêu
    private Border? _ghost;

    private Control? HeaderContainer(int i) => _headerItems.ContainerFromIndex(i);
    private Control? LaneContainer(int i) => _laneItems.ContainerFromIndex(i);
    private double ContentY(PointerEventArgs e) => e.GetPosition(_headerScroll).Y + _headerScroll.Offset.Y;

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is null || _dragFinishing || !e.GetCurrentPoint(_headerScroll).Properties.IsLeftButtonPressed) return;
        if (e.Source is not Visual src || !IsInGrip(src)) return;

        int index = (int)Math.Floor(ContentY(e) / TrackRowHeight);
        if (index < 0 || index >= Vm.Tracks.Count || Vm.Tracks[index].IsLocked) return;

        _dragFrom = _dragTarget = index;
        _dragActive = false;
        _pressY = e.GetPosition(_headerScroll).Y;
        _grabOffset = ContentY(e) - index * TrackRowHeight;
        e.Pointer.Capture(_headerScroll);
        e.Handled = true;
    }

    private void OnHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_dragFrom < 0 || _dragFinishing || Vm is null) return;

        if (!_dragActive)
        {
            if (Math.Abs(e.GetPosition(_headerScroll).Y - _pressY) < DragThreshold) return;
            BeginDrag();
        }

        int count = Vm.Tracks.Count;
        double ghostTop = DragGhostTop(ContentY(e), count);
        PlaceGhost(ghostTop);

        int target = Math.Clamp((int)Math.Round(ghostTop / TrackRowHeight), 0, count - 1);
        if (target != _dragTarget)
        {
            _dragTarget = target;
            ApplyShifts(_dragFrom, target, count);
        }
    }

    private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragFrom < 0 || _dragFinishing) return;
        e.Pointer.Capture(null);
        if (!_dragActive) { ResetDragState(); return; }
        EndDrag(commit: true);
    }

    private void OnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragFrom >= 0 && !_dragFinishing) EndDrag(commit: false);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (HandleShortcut(e)) { e.Handled = true; return; }

        if (Vm is { IsPianoRoll: true } roll && FocusManager?.GetFocusedElement() is not TextBox)
        {
            if (e.Key == Key.Escape) { roll.ClosePianoRollCommand.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.Delete) { roll.DeleteSelectedNote(); e.Handled = true; return; }
            if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.S) { roll.SplitNoteCommand.Execute(null); e.Handled = true; return; }
            if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.M) { roll.MergeNoteCommand.Execute(null); e.Handled = true; return; }
            if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Q) { roll.QuantizeNotesCommand.Execute(null); e.Handled = true; return; }
        }

        if (e.Key != Key.Escape || _dragFrom < 0 || _dragFinishing) return;
        e.Handled = true;
        if (_dragActive) EndDrag(commit: false);
        else ResetDragState();
    }

    /// <summary>Vị trí mép trên của ghost (tọa độ nội dung), giới hạn trong danh sách track.</summary>
    public static double DragGhostTopFor(double contentY, double grabOffset, int count)
        => Math.Clamp(contentY - grabOffset, 0, Math.Max(0, (count - 1) * TrackRowHeight));

    private double DragGhostTop(double contentY, int count) => DragGhostTopFor(contentY, _grabOffset, count);

    /// <summary>Độ dịch dọc của hàng <paramref name="i"/> khi kéo <paramref name="from"/> tới <paramref name="target"/>.</summary>
    public static double ShiftFor(int i, int from, int target)
    {
        if (i == from) return 0;
        if (from < target && i > from && i <= target) return -TrackRowHeight;
        if (from > target && i >= target && i < from) return TrackRowHeight;
        return 0;
    }

    private void BeginDrag()
    {
        _dragActive = true;
        var header = HeaderContainer(_dragFrom);
        if (header is not null) _ghost = BuildGhost(header);
        if (_ghost is not null) _dragLayer.Children.Add(_ghost);
        PlaceGhost(_dragFrom * TrackRowHeight);

        // Hàng gốc thành ô trống (cả header lẫn lane); các hàng khác chuẩn bị trượt.
        if (header is not null) header.Opacity = 0;
        if (LaneContainer(_dragFrom) is { } lane) lane.Opacity = 0;
        int count = Vm!.Tracks.Count;
        for (int i = 0; i < count; i++)
        {
            if (i == _dragFrom) continue;
            EnableSlide(HeaderContainer(i));
            EnableSlide(LaneContainer(i));
        }
    }

    private Border? BuildGhost(Control source)
    {
        double w = source.Bounds.Width, h = source.Bounds.Height;
        if (w < 1 || h < 1) return null;
        try
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            var bitmap = new RenderTargetBitmap(
                new PixelSize(Math.Max(1, (int)Math.Ceiling(w * scale)), Math.Max(1, (int)Math.Ceiling(h * scale))),
                new Vector(96 * scale, 96 * scale));
            bitmap.Render(source);
            return new Border
            {
                Width = w,
                Height = h,
                Opacity = 0.93,
                BoxShadow = BoxShadows.Parse("0 6 18 0 #AA000000"),
                Child = new Image { Source = bitmap, Width = w, Height = h, Stretch = Stretch.Fill },
            };
        }
        catch
        {
            // Không chụp được thì dùng khối màu thay thế.
            return new Border { Width = w, Height = h, Background = new SolidColorBrush(Color.Parse("#2A2A2A")), Opacity = 0.9 };
        }
    }

    private void PlaceGhost(double contentTop)
    {
        if (_ghost is null) return;
        var p = _headerScroll.TranslatePoint(new Point(0, contentTop - _headerScroll.Offset.Y), _dragLayer) ?? default;
        Canvas.SetLeft(_ghost, p.X);
        Canvas.SetTop(_ghost, p.Y);
    }

    private static void EnableSlide(Control? c)
    {
        if (c is null) return;
        c.Transitions =
        [
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = SlideTime },
        ];
        c.RenderTransform = TransformOperations.Parse("translateY(0px)");
    }

    private void ApplyShifts(int from, int target, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (i == from) continue;
            var ops = TransformOperations.Parse(
                $"translateY({ShiftFor(i, from, target).ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
            if (HeaderContainer(i) is { } h) h.RenderTransform = ops;
            if (LaneContainer(i) is { } l) l.RenderTransform = ops;
        }
    }

    /// <summary>Kết thúc kéo: ghost trượt về ô đích (thả) hoặc ô gốc (Esc), rồi dọn dẹp.</summary>
    private void EndDrag(bool commit)
    {
        _dragFinishing = true;
        int from = _dragFrom;
        int target = commit ? _dragTarget : from;

        if (!commit && Vm is not null) ApplyShifts(from, from, Vm.Tracks.Count); // các hàng về chỗ cũ

        if (_ghost is not null)
        {
            _ghost.Transitions = [new DoubleTransition { Property = Canvas.TopProperty, Duration = SlideTime }];
            PlaceGhost(target * TrackRowHeight);
        }

        _ = FinishDragAsync(commit, from, target);
    }

    private async System.Threading.Tasks.Task FinishDragAsync(bool commit, int from, int target)
    {
        // Đợi hoạt ảnh trượt xong rồi mới dọn và (nếu thả) đổi chỗ thật.
        await System.Threading.Tasks.Task.Delay(SlideTime + TimeSpan.FromMilliseconds(30));
        ClearDragVisuals();
        if (commit && target != from) Vm?.MoveTrack(from, target);
        ResetDragState();
    }

    private void ClearDragVisuals()
    {
        if (_ghost is not null) _dragLayer.Children.Remove(_ghost);
        _ghost = null;
        int count = Vm?.Tracks.Count ?? 0;
        for (int i = 0; i < count; i++)
        {
            foreach (var c in new[] { HeaderContainer(i), LaneContainer(i) })
            {
                if (c is null) continue;
                c.Transitions = null;
                c.RenderTransform = null;
                c.Opacity = 1;
            }
        }
    }

    private void ResetDragState()
    {
        _dragFrom = _dragTarget = -1;
        _dragActive = false;
        _dragFinishing = false;
    }

    private static bool IsInGrip(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
        {
            if (cur is Border b && b.Classes.Contains("grip")) return true;
        }
        return false;
    }

    // ---------- project (.smx) ----------

    private enum UnsavedChoice { Save, Discard, Cancel }

    private static readonly FilePickerFileType ProjectFileType =
        new("SoundMaster project") { Patterns = ["*.smx"] };

    /// <summary>Hỏi lưu khi đóng cửa sổ có thay đổi chưa lưu. Tắt trong test giao diện.</summary>
    public bool ConfirmOnClose { get; set; } = true;

    private bool _closeConfirmed;

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!ConfirmOnClose || _closeConfirmed || Vm is not { IsDirty: true }) return;

        e.Cancel = true; // giữ cửa sổ lại cho tới khi người dùng chọn
        if (await ResolveUnsavedAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    /// <summary>
    /// Nếu có thay đổi chưa lưu thì hỏi Lưu / Không lưu / Hủy. Trả về true nếu được phép tiếp tục
    /// (đã lưu, bỏ qua, hoặc không có gì để lưu).
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ResolveUnsavedAsync()
    {
        if (Vm is not { IsDirty: true } vm) return true;

        switch (await AskUnsavedAsync())
        {
            case UnsavedChoice.Save:
                return await SaveProjectAsync(forcePicker: false);
            case UnsavedChoice.Discard:
                return true;
            default:
                return false;
        }
    }

    private async System.Threading.Tasks.Task<UnsavedChoice> AskUnsavedAsync()
    {
        string name = Vm?.ProjectPath is { } p ? System.IO.Path.GetFileName(p) : "project chưa đặt tên";
        int index = await AskAsync($"Lưu thay đổi cho “{name}” trước khi tiếp tục?", "Hủy", "Không lưu", "Lưu");
        return index switch
        {
            2 => UnsavedChoice.Save,
            1 => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    /// <summary>Hộp thoại hỏi với các nút cho trước (nút cuối là nút chính, màu vàng). Trả về chỉ số nút đã bấm; đóng cửa sổ = 0.</summary>
    private async System.Threading.Tasks.Task<int> AskAsync(string message, params string[] buttons)
    {
        int chosen = 0;
        var dialog = new Window
        {
            Title = "SoundMaster",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10,
        };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 96, Padding = new Thickness(14, 8) };
            if (i == buttons.Length - 1) b.Classes.Add("gold");
            b.Click += (_, _) =>
            {
                chosen = index;
                dialog.Close();
            };
            row.Children.Add(b);
        }

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                row,
            },
        };

        await dialog.ShowDialog(this);
        return chosen;
    }

    /// <summary>Khi mở app: nếu phiên trước bị đóng đột ngột và còn bản tự lưu thì hỏi có khôi phục không.</summary>
    private async System.Threading.Tasks.Task CheckRecoveryAsync()
    {
        if (Vm?.FindRecovery() is not { } info) return;

        string what = info.OriginalPath is { } original ? $"project “{System.IO.Path.GetFileName(original)}”" : "một project chưa lưu";
        string when = info.SavedAtUtc.ToLocalTime().ToString("HH:mm dd/MM/yyyy");
        int choice = await AskAsync(
            $"Phiên trước của SoundMaster bị đóng đột ngột. Có bản tự lưu của {what} lúc {when}.\n\nKhôi phục lại bản này?",
            "Bỏ bản tự lưu", "Khôi phục");

        if (choice == 1) await Vm.RecoverAsync(info);
        else Vm.DiscardRecovery();
    }

    private async void OnNewProject(object? sender, RoutedEventArgs e)
    {
        if (Vm is null || !await ResolveUnsavedAsync()) return;
        Vm.NewProject();
    }

    private async void OnOpenProject(object? sender, RoutedEventArgs e) => await OpenProjectAsync();

    private async void OnSaveProject(object? sender, RoutedEventArgs e) => await SaveProjectAsync(forcePicker: false);

    private async void OnSaveProjectAs(object? sender, RoutedEventArgs e) => await SaveProjectAsync(forcePicker: true);

    private async System.Threading.Tasks.Task OpenProjectAsync()
    {
        if (Vm is null || !await ResolveUnsavedAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Mở project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectFileType],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await Vm.OpenProjectAsync(path);
    }

    /// <summary>Lưu project; chưa có đường dẫn (hoặc <paramref name="forcePicker"/>) thì hỏi nơi lưu. Trả về true nếu đã lưu.</summary>
    private async System.Threading.Tasks.Task<bool> SaveProjectAsync(bool forcePicker)
    {
        if (Vm is null) return false;
        string? path = forcePicker ? null : Vm.ProjectPath;
        if (path is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Lưu project",
                SuggestedFileName = Vm.SongFileName + ".smx",
                DefaultExtension = "smx",
                FileTypeChoices = [ProjectFileType],
            });
            path = file?.TryGetLocalPath();
            if (path is null) return false;
            if (!path.EndsWith(".smx", StringComparison.OrdinalIgnoreCase)) path += ".smx";
        }

        try
        {
            Vm.SaveProject(path);
            return true;
        }
        catch (Exception ex)
        {
            Vm.Status = $"Không lưu được project: {ex.Message}";
            return false;
        }
    }

    /// <summary>Phím tắt: Ctrl+S / Ctrl+Shift+S / Ctrl+O / Ctrl+Z / Ctrl+Y (Z, Y nhường cho ô nhập chữ đang focus).</summary>
    private bool HandleShortcut(KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || Vm is null) return false;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        bool typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

        switch (e.Key)
        {
            case Key.S:
                _ = SaveProjectAsync(forcePicker: shift);
                return true;
            case Key.O:
                _ = OpenProjectAsync();
                return true;
            case Key.Z when !typing:
                if (shift) Vm.RedoCommand.Execute(null);
                else Vm.UndoCommand.Execute(null);
                return true;
            case Key.Y when !typing:
                Vm.RedoCommand.Execute(null);
                return true;
            default:
                return false;
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

    private async void OnOpenMidi(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Chọn file MIDI",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("MIDI") { Patterns = ["*.mid", "*.midi"] }],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await Vm.ImportMidiAsync(path);
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
        var midi = paths.Where(MainViewModel.IsMidiFile).ToList();
        var audio = paths.Except(midi).ToList();
        if (audio.Count > 0) await Vm.LoadFilesAsync(audio, replace: false);
        foreach (var m in midi) await Vm.ImportMidiAsync(m);
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
        var midi = paths.Where(MainViewModel.IsMidiFile).ToList();
        var audio = paths.Except(midi).ToList();
        if (audio.Count > 0) await Vm.LoadFilesAsync(audio, replace: false);
        foreach (var m in midi) await Vm.ImportMidiAsync(m);
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
