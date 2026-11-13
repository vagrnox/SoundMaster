using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using SoundMaster.App.ViewModels;
using SoundMaster.Core.Audio;
using SoundMaster.Core.Timeline;

namespace SoundMaster.App.Controls;

/// <summary>
/// Một lane: waveform dạng cột đứng theo màu track, cộng với vùng chọn.
/// <list type="bullet">
/// <item>Bấm: chọn track (nền lane sáng lên). Bấm không tua — chỉ thước nhịp của track tổng mới tua.</item>
/// <item>Kéo: tạo vùng chọn (viền vàng); phần ngoài vùng của track chuyển xám.</item>
/// <item>Hai cạnh dày của vùng đang chọn kéo được để đổi khoảng.</item>
/// <item>Chuột phải trên vùng: menu hiệu ứng (Fade in/out, Loop, Silence, Reverse, bỏ hiệu ứng...).</item>
/// </list>
/// Chỉ vẽ phần đang nhìn thấy (theo OffsetSeconds / PixelsPerSecond).
/// </summary>
public class WaveformView : Control
{
    public static readonly StyledProperty<AudioTrack?> TrackProperty =
        AvaloniaProperty.Register<WaveformView, AudioTrack?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<WaveformView, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WaveformView, string?>(nameof(Title));

    public static readonly StyledProperty<double> PixelsPerSecondProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(PixelsPerSecond), 20);

    public static readonly StyledProperty<double> OffsetSecondsProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(OffsetSeconds));

    public static readonly StyledProperty<double> BpmProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Bpm), 120);

    public static readonly StyledProperty<int> BeatsPerBarProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(BeatsPerBar), 4);

    private const double HandleWidth = 4;     // độ dày hai cạnh của vùng đang chọn
    private const double HandleGrab = 6;      // vùng bấm quanh mỗi cạnh
    private const double CreateThreshold = 4; // kéo tối thiểu (px) mới tạo vùng

    private static readonly Color PrimaryColor = Color.Parse("#F1AB29");
    private static readonly ImmutableSolidColorBrush PrimaryBrush = new(PrimaryColor);
    private static readonly IBrush TintActive = new ImmutableSolidColorBrush(PrimaryColor, 0.07);
    private static readonly IBrush TintIdle = new ImmutableSolidColorBrush(PrimaryColor, 0.04);
    private static readonly IBrush GreyBars = new ImmutableSolidColorBrush(Color.Parse("#5B6068"));
    private static readonly IBrush BadgeInk = new ImmutableSolidColorBrush(Color.Parse("#111111"));
    private static readonly IPen ActivePen = new ImmutablePen(PrimaryBrush, 1);
    private static readonly IPen IdlePen = new ImmutablePen(new ImmutableSolidColorBrush(PrimaryColor, 0.5), 1);
    private static readonly IBrush Transparent = Brushes.Transparent;
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);

    private enum DragMode { None, Creating, ResizeLeft, ResizeRight }

    private TrackViewModel? _vm;
    private DragMode _mode;
    private double _anchorSeconds;
    private double _pressX;
    private RegionViewModel? _created;

    static WaveformView()
    {
        AffectsRender<WaveformView>(TrackProperty, TrackBrushProperty, TitleProperty, PixelsPerSecondProperty,
            OffsetSecondsProperty, BpmProperty, BeatsPerBarProperty);
    }

    public double Bpm { get => GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public int BeatsPerBar { get => GetValue(BeatsPerBarProperty); set => SetValue(BeatsPerBarProperty, value); }

    public AudioTrack? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public double PixelsPerSecond { get => GetValue(PixelsPerSecondProperty); set => SetValue(PixelsPerSecondProperty, value); }
    public double OffsetSeconds { get => GetValue(OffsetSecondsProperty); set => SetValue(OffsetSecondsProperty, value); }

    // ---------- theo dõi view-model để vẽ lại khi vùng đổi ----------

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Detach();
        _vm = DataContext as TrackViewModel;
        if (_vm is null) return;

        _vm.Regions.CollectionChanged += OnRegionsCollectionChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
        foreach (var r in _vm.Regions) r.PropertyChanged += OnRegionPropertyChanged;
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.Regions.CollectionChanged -= OnRegionsCollectionChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        foreach (var r in _vm.Regions) r.PropertyChanged -= OnRegionPropertyChanged;
        _vm = null;
    }

    private void OnRegionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (RegionViewModel r in e.OldItems) r.PropertyChanged -= OnRegionPropertyChanged;
        if (e.NewItems is not null)
            foreach (RegionViewModel r in e.NewItems) r.PropertyChanged += OnRegionPropertyChanged;
        InvalidateVisual();
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackViewModel.ActiveRegion) or nameof(TrackViewModel.IsSelected)
            or nameof(TrackViewModel.IsLocked))
            InvalidateVisual();
    }

    // ---------- vẽ ----------

    private double XAt(double seconds) => (seconds - OffsetSeconds) * PixelsPerSecond;
    private double TimeAt(double x) => OffsetSeconds + x / PixelsPerSecond;

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        // Nền trong suốt để lane nhận được chuột ngay cả ngoài vùng có tiếng.
        context.DrawRectangle(Transparent, null, new Rect(0, 0, w, h));

        var track = Track;
        double pps = PixelsPerSecond;
        if (track is null || pps <= 0 || w <= 0 || track.FrameCount == 0) return;

        var regions = _vm?.Regions;
        var active = _vm?.ActiveRegion;

        // Nền nhạt của các vùng (dưới waveform).
        if (regions is not null)
        {
            foreach (var r in regions)
            {
                if (!TryClip(r, w, out double rx0, out double rx1)) continue;
                context.DrawRectangle(ReferenceEquals(r, active) ? TintActive : TintIdle, null,
                    new Rect(rx0, 1, rx1 - rx0, h - 2));
            }
        }

        double duration = track.Duration.TotalSeconds;
        double x0 = Math.Max(0, XAt(0));
        double x1 = Math.Min(w, XAt(duration));
        if (x1 > x0)
        {
            (double From, double To)? keep = active is null ? null : (XAt(active.StartSeconds), XAt(active.EndSeconds));
            DrawBars(context, track, pps, x0, x1, h, keep);
        }

        if (regions is null) return;
        foreach (var r in regions)
        {
            if (TryClip(r, w, out _, out _)) DrawRegionFrame(context, r, ReferenceEquals(r, active), w, h);
        }
    }

    private bool TryClip(RegionViewModel r, double w, out double x0, out double x1)
    {
        x0 = XAt(r.StartSeconds);
        x1 = XAt(r.EndSeconds);
        if (x1 < 0 || x0 > w) return false;
        x0 = Math.Max(x0, -HandleWidth);
        x1 = Math.Min(x1, w + HandleWidth);
        return x1 > x0;
    }

    /// <summary>
    /// Waveform dạng cột đứng mảnh (rộng 2 px, cách 4 px) theo màu track, đối xứng quanh trục giữa.
    /// Cột được căn theo tọa độ thế giới nên khi cuộn các cột không nhấp nháy.
    /// Cột ngoài <paramref name="keep"/> (nếu có) được vẽ màu xám.
    /// </summary>
    private void DrawBars(DrawingContext context, AudioTrack track, double pps, double x0, double x1, double h,
        (double From, double To)? keep)
    {
        const double step = 4, barWidth = 2;
        double bucketSec = AudioTrack.PeakBucketFrames / (double)EngineFormat.SampleRate;
        double mid = h / 2;
        double maxAmp = h / 2 - 8;
        var brush = TrackBrush ?? Brushes.Gray;

        double worldLeft = OffsetSeconds * pps;
        int firstCol = (int)Math.Floor((worldLeft + x0) / step);
        int lastCol = (int)Math.Ceiling((worldLeft + x1) / step);

        for (int c = firstCol; c < lastCol; c++)
        {
            double x = c * step - worldLeft;
            double t0 = c * step / pps;
            double t1 = (c + 1) * step / pps;
            int b0 = (int)Math.Floor(t0 / bucketSec);
            int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(t1 / bucketSec));
            double peak = TimelineMath.MaxPeak(track.Peaks, b0, b1);

            // Im lặng vẫn vẽ chấm 1 px để thành đường đứt nét ở giữa.
            double amp = Math.Max(0.5, peak * maxAmp);
            var fill = keep is { } k && (x + barWidth / 2 < k.From || x + barWidth / 2 > k.To) ? GreyBars : brush;
            context.DrawRectangle(fill, null, new Rect(x, mid - amp, barWidth, amp * 2), 1, 1);
        }
    }

    /// <summary>Viền vàng của vùng: vùng đang chọn có hai cạnh dày; vùng đã có hiệu ứng thì mờ hơn. Kèm nhãn hiệu ứng.</summary>
    private void DrawRegionFrame(DrawingContext context, RegionViewModel r, bool isActive, double w, double h)
    {
        double x0 = XAt(r.StartSeconds), x1 = XAt(r.EndSeconds);
        var frame = new Rect(x0, 1.5, Math.Max(1, x1 - x0), h - 3);
        context.DrawRectangle(null, isActive ? ActivePen : IdlePen, frame, 3, 3);

        if (isActive)
        {
            // Hai cạnh dày để bấm vào đổi khoảng.
            context.DrawRectangle(PrimaryBrush, null, new Rect(x0 - 1, 1, HandleWidth, h - 2), 2, 2);
            context.DrawRectangle(PrimaryBrush, null, new Rect(x1 - HandleWidth + 1, 1, HandleWidth, h - 2), 2, 2);
        }

        string text = r.BadgeText;
        if (text.Length == 0) return;

        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 11, BadgeInk);
        double bw = label.Width + 16, bh = label.Height + 4;
        double visibleLeft = Math.Max(x0, 0), visibleRight = Math.Min(x1, w);
        double cx = (visibleLeft + visibleRight) / 2;      // căn giữa phần vùng đang nhìn thấy
        double bx = Math.Clamp(cx - bw / 2, 2, Math.Max(2, w - bw - 2));
        context.DrawRectangle(PrimaryBrush, null, new Rect(bx, 4, bw, bh), bh / 2, bh / 2);
        context.DrawText(label, new Point(bx + 8, 4 + 2));
    }

    // ---------- chuột ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var vm = _vm;
        if (vm is null || PixelsPerSecond <= 0) return;

        vm.RequestSelect(); // bấm bất kỳ đâu trên lane là chọn track (không tua)

        var props = e.GetCurrentPoint(this).Properties;
        double x = e.GetPosition(this).X;
        double t = TimeAt(x);

        if (props.IsRightButtonPressed)
        {
            var hit = vm.RegionAt(t);
            if (hit is null || vm.IsLocked)
            {
                ContextMenu = null;
                return;
            }
            vm.ActivateRegion(hit);
            ContextMenu = BuildMenu(vm, hit);
            return;
        }

        if (!props.IsLeftButtonPressed || vm.IsLocked) return;

        // Bấm vào cạnh của vùng đang chọn: đổi khoảng.
        if (vm.ActiveRegion is { } active)
        {
            double dl = Math.Abs(x - XAt(active.StartSeconds)), dr = Math.Abs(x - XAt(active.EndSeconds));
            if (Math.Min(dl, dr) <= HandleGrab)
            {
                _mode = dl <= dr ? DragMode.ResizeLeft : DragMode.ResizeRight;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        // Bấm vào trong một vùng: chọn vùng đó.
        if (vm.RegionAt(t) is { } inside)
        {
            vm.ActivateRegion(inside);
            e.Handled = true;
            return;
        }

        // Bấm ra ngoài: bỏ vùng đang chọn (nếu trống) và chuẩn bị kéo tạo vùng mới.
        vm.DeactivateRegion();
        _mode = DragMode.Creating;
        _anchorSeconds = Math.Clamp(t, 0, vm.DurationSeconds);
        _pressX = x;
        _created = null;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var vm = _vm;
        if (vm is null || PixelsPerSecond <= 0) return;

        double x = e.GetPosition(this).X;
        double t = Math.Clamp(TimeAt(x), 0, vm.DurationSeconds);

        switch (_mode)
        {
            case DragMode.Creating:
                if (_created is null)
                {
                    if (Math.Abs(x - _pressX) < CreateThreshold) return;
                    _created = vm.BeginRegion(_anchorSeconds, t);
                }
                else
                {
                    _created.StartSeconds = Math.Min(_anchorSeconds, t);
                    _created.EndSeconds = Math.Max(_anchorSeconds, t);
                }
                break;

            case DragMode.ResizeLeft when vm.ActiveRegion is { } left:
                left.StartSeconds = Math.Min(t, left.EndSeconds - RegionViewModel.MinLengthSeconds);
                break;

            case DragMode.ResizeRight when vm.ActiveRegion is { } right:
                right.EndSeconds = Math.Max(t, right.StartSeconds + RegionViewModel.MinLengthSeconds);
                break;

            default:
                UpdateHoverCursor(vm, x);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var vm = _vm;
        var mode = _mode;
        _mode = DragMode.None;
        if (mode == DragMode.None || vm is null) return;

        e.Pointer.Capture(null);
        if (mode == DragMode.Creating)
        {
            // Vùng quá ngắn coi như chỉ là một cú bấm.
            if (_created is { LengthSeconds: < RegionViewModel.MinLengthSeconds }) vm.DeactivateRegion();
            _created = null;
        }
        else
        {
            vm.NotifyRegionsChanged(); // vùng đổi khoảng: cập nhật hiệu ứng / vùng lặp theo khoảng mới
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_mode == DragMode.None) Cursor = null;
    }

    private void UpdateHoverCursor(TrackViewModel vm, double x)
    {
        bool nearEdge = !vm.IsLocked && vm.ActiveRegion is { } a
            && Math.Min(Math.Abs(x - XAt(a.StartSeconds)), Math.Abs(x - XAt(a.EndSeconds))) <= HandleGrab;
        Cursor = nearEdge ? ResizeCursor : null;
    }

    // ---------- menu chuột phải ----------

    private static ContextMenu BuildMenu(TrackViewModel vm, RegionViewModel region)
    {
        MenuItem Effect(string header, RegionEffect effect) => ToItem(header, region.Effect == effect,
            () => vm.SetEffect(region, effect));

        var menu = new ContextMenu();
        menu.Items.Add(Effect("Fade in", RegionEffect.FadeIn));
        menu.Items.Add(Effect("Fade out", RegionEffect.FadeOut));
        menu.Items.Add(Effect("Loop", RegionEffect.Loop));
        menu.Items.Add(Effect("Silence", RegionEffect.Silence));
        menu.Items.Add(Effect("Reverse", RegionEffect.Reverse));
        menu.Items.Add(new Separator());

        var clear = ToItem("Bỏ hiệu ứng", false, () => vm.SetEffect(region, RegionEffect.None));
        clear.IsEnabled = region.Effect != RegionEffect.None;
        menu.Items.Add(clear);
        menu.Items.Add(ToItem("Xóa vùng chọn", false, () => vm.RemoveRegion(region)));
        return menu;
    }

    private static MenuItem ToItem(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header };
        if (isChecked) item.Icon = new TextBlock { Text = "✓", Foreground = PrimaryBrush };
        item.Click += (_, _) => action();
        return item;
    }
}
