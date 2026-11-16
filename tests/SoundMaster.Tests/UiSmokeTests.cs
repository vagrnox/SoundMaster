using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SoundMaster.App.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SoundMaster.App;
using SoundMaster.App.ViewModels;
using SoundMaster.App.Views;
using SoundMaster.Core.Audio;
using SoundMaster.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace SoundMaster.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<global::SoundMaster.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Dựng cửa sổ thật trong môi trường headless để bắt lỗi XAML/binding lúc chạy.</summary>
public class UiSmokeTests
{
    private static MainWindow CreateWindow(out MainViewModel vm)
    {
        vm = new MainViewModel(enableAudioOutput: false);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800, ConfirmOnClose = false };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void MainWindow_Opens_WithoutTracks()
    {
        var window = CreateWindow(out var vm);
        Assert.Empty(vm.Tracks);
        window.Close();
    }

    [AvaloniaFact]
    public void MainWindow_ShowsDemoTracks_AndRenders()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(5, vm.Tracks.Count);
        Assert.True(vm.Timeline.ViewportWidth > 0);
        Assert.True(vm.Timeline.PixelsPerSecond > 2); // đã zoom vừa khít bài

        // Tua bằng lệnh của timeline, giống khi bấm lên thước.
        vm.Timeline.SeekCommand.Execute(5.0);
        Assert.Equal(5.0, vm.Timeline.PositionSeconds, 3);

        // Phân tích chạy nền: đợi xong để ảnh chụp có LUFS / giọng.
        for (int i = 0; i < 200 && vm.IsAnalyzing; i++)
        {
            Thread.Sleep(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.False(vm.IsAnalyzing);
        Assert.NotNull(vm.Analysis.Lufs);
        Assert.NotNull(vm.Analysis.Key);

        // Khóa một track để ảnh chụp cho thấy trạng thái khóa; đặt volume và mức tín hiệu giả
        // để thấy thanh volume (vạch xanh) và đồng hồ mức gradient bên trong.
        vm.Tracks[1].IsLocked = true;
        vm.Tracks[0].GainDb = -6;
        double[] meters = [-8, -14, -20, -3, -30];
        for (int i = 0; i < meters.Length; i++)
        {
            vm.Tracks[i].MeterLeftDb = meters[i];
            vm.Tracks[i].MeterRightDb = meters[i] - 2;
        }
        // Vùng chọn mẫu: Fade in trên track 1, Loop trên track 4, và vùng đang chọn (chưa có hiệu ứng) trên track 3.
        var fadeRegion = vm.Tracks[0].BeginRegion(2.0, 6.0);
        vm.Tracks[0].SetEffect(fadeRegion, RegionEffect.FadeIn);
        var loopRegion = vm.Tracks[3].BeginRegion(8.0, 12.0);
        vm.Tracks[3].SetEffect(loopRegion, RegionEffect.Loop);
        vm.Tracks[4].Notes = SoundMaster.Core.Transcription.NoteTranscriber.Transcribe(vm.Tracks[4].Audio); // bóc nốt mẫu cho ảnh chụp
        vm.Tracks[2].RequestSelect();
        vm.Tracks[2].BeginRegion(3.0, 9.0);
        vm.MasterMeterLeftDb = -5;
        vm.MasterMeterRightDb = -7;
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        string path = Path.Combine(Path.GetTempPath(), "soundmaster_ui.png");
        frame!.Save(path);
        window.Close();
    }


    [AvaloniaFact]
    public void MoveAndRemoveTracks_ReorderRenumberAndRespectLock()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var names = vm.Tracks.Select(t => t.Name).ToList();

        Assert.True(vm.MoveTrack(0, 2));
        Assert.Equal([names[1], names[2], names[0], names[3], names[4]], vm.Tracks.Select(t => t.Name).ToArray());
        Assert.Equal([1, 2, 3, 4, 5], vm.Tracks.Select(t => t.Index).ToArray());

        // Track khóa không di chuyển / xóa được.
        vm.Tracks[0].IsLocked = true;
        Assert.False(vm.MoveTrack(0, 3));
        vm.RemoveTrackCommand.Execute(vm.Tracks[0]);
        Assert.Equal(5, vm.Tracks.Count);
        vm.Tracks[0].IsLocked = false;

        var victim = vm.Tracks[1];
        vm.RemoveTrackCommand.Execute(victim);
        Assert.Equal(4, vm.Tracks.Count);
        Assert.DoesNotContain(victim, vm.Tracks);
        Assert.Equal([1, 2, 3, 4], vm.Tracks.Select(t => t.Index).ToArray());

        while (vm.Tracks.Count > 0) vm.RemoveTrackCommand.Execute(vm.Tracks[0]);
        Assert.Equal(0, vm.Timeline.DurationSeconds);
        window.Close();
    }

    private static void Pump(int ms)
    {
        for (int waited = 0; waited < ms; waited += 20)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Point GripCenter(MainWindow window, MainViewModel vm, int trackIndex)
    {
        var header = window.GetVisualDescendants().OfType<TrackHeaderView>()
            .First(h => ReferenceEquals(h.DataContext, vm.Tracks[trackIndex]));
        var grip = header.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("grip"));
        return grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
    }

    [AvaloniaFact]
    public void DragReorder_DropMovesTrack_AndEscapeCancels()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var original = vm.Tracks.Select(t => t.Name).ToArray();

        // Kéo track 1 xuống qua hai hàng rồi nhấn Esc: thứ tự không đổi.
        var p = GripCenter(window, vm, 0);
        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(new Point(p.X, p.Y + 10));
        window.MouseMove(new Point(p.X, p.Y + 2 * TrackViewModel.RowHeight + 10));
        Dispatcher.UIThread.RunJobs();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Pump(300);
        window.MouseUp(new Point(p.X, p.Y + 150), MouseButton.Left);
        Pump(100);
        Assert.Equal(original, vm.Tracks.Select(t => t.Name).ToArray());

        // Kéo lần nữa và thả: track đầu xuống vị trí thứ 3.
        p = GripCenter(window, vm, 0);
        window.MouseDown(p, MouseButton.Left);
        window.MouseMove(new Point(p.X, p.Y + 10));
        window.MouseMove(new Point(p.X, p.Y + 2 * TrackViewModel.RowHeight + 6));
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(new Point(p.X, p.Y + 2 * TrackViewModel.RowHeight + 6), MouseButton.Left);
        Pump(400);
        Assert.Equal([original[1], original[2], original[0], original[3], original[4]],
            vm.Tracks.Select(t => t.Name).ToArray());
        window.Close();
    }

    [Fact]
    public void DragMath_ShiftsRowsBetweenSourceAndTarget()
    {
        double r = TrackViewModel.RowHeight;
        // Kéo hàng 1 xuống hàng 3: các hàng 2,3 trượt lên.
        Assert.Equal(0, MainWindow.ShiftFor(1, 1, 3));
        Assert.Equal(-r, MainWindow.ShiftFor(2, 1, 3));
        Assert.Equal(-r, MainWindow.ShiftFor(3, 1, 3));
        Assert.Equal(0, MainWindow.ShiftFor(4, 1, 3));
        Assert.Equal(0, MainWindow.ShiftFor(0, 1, 3));
        // Kéo hàng 3 lên hàng 1: các hàng 1,2 trượt xuống.
        Assert.Equal(r, MainWindow.ShiftFor(1, 3, 1));
        Assert.Equal(r, MainWindow.ShiftFor(2, 3, 1));
        Assert.Equal(0, MainWindow.ShiftFor(3, 3, 1));
        Assert.Equal(0, MainWindow.ShiftFor(0, 3, 1));
        // Ghost không ra ngoài danh sách.
        Assert.Equal(0, MainWindow.DragGhostTopFor(-50, 10, 5));
        Assert.Equal(4 * r, MainWindow.DragGhostTopFor(9999, 10, 5));
    }

    private static WaveformView LaneOf(MainWindow window, TrackViewModel track)
        => window.GetVisualDescendants().OfType<WaveformView>().First(l => ReferenceEquals(l.DataContext, track));

    [AvaloniaFact]
    public void Lane_Click_SelectsTrack_WithoutSeeking_AndDragCreatesResizableRegion()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var track = vm.Tracks[2];
        var lane = LaneOf(window, track);
        Point At(double x) => lane.TranslatePoint(new Point(x, 30), window)!.Value;
        double positionBefore = vm.Timeline.PositionSeconds;

        // Bấm: chọn track, không tua.
        window.MouseDown(At(100), MouseButton.Left);
        window.MouseUp(At(100), MouseButton.Left);
        Assert.True(track.IsSelected);
        Assert.All(vm.Tracks.Where(t => !ReferenceEquals(t, track)), t => Assert.False(t.IsSelected));
        Assert.Equal(positionBefore, vm.Timeline.PositionSeconds);
        Assert.Empty(track.Regions);

        // Kéo: tạo vùng, vùng trở thành vùng đang chọn.
        window.MouseDown(At(100), MouseButton.Left);
        window.MouseMove(At(120));
        window.MouseMove(At(300));
        window.MouseUp(At(300), MouseButton.Left);
        var region = Assert.Single(track.Regions);
        Assert.Same(region, track.ActiveRegion);
        double pps = vm.Timeline.PixelsPerSecond;
        Assert.Equal(200 / pps, region.LengthSeconds, 2);
        Assert.Equal(positionBefore, vm.Timeline.PositionSeconds);

        // Kéo cạnh phải ra thêm 60 px: vùng dài thêm 60/pps giây.
        double endX = (region.EndSeconds - vm.Timeline.OffsetSeconds) * pps;
        double endBefore = region.EndSeconds;
        window.MouseDown(At(endX), MouseButton.Left);
        window.MouseMove(At(endX + 30));
        window.MouseMove(At(endX + 60));
        window.MouseUp(At(endX + 60), MouseButton.Left);
        Assert.Equal(endBefore + 60 / pps, region.EndSeconds, 2);
        Assert.Single(track.Regions);

        // Bấm ra ngoài vùng (không kéo): vùng trống bị bỏ.
        window.MouseDown(At(900), MouseButton.Left);
        window.MouseUp(At(900), MouseButton.Left);
        Assert.Empty(track.Regions);
        window.Close();
    }

    [AvaloniaFact]
    public void Lane_RightClickOnRegion_OffersEffectsMenu()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var track = vm.Tracks[0];
        var lane = LaneOf(window, track);
        Point At(double x) => lane.TranslatePoint(new Point(x, 30), window)!.Value;

        window.MouseDown(At(100), MouseButton.Left);
        window.MouseMove(At(150));
        window.MouseMove(At(400));
        window.MouseUp(At(400), MouseButton.Left);
        Assert.Single(track.Regions);

        window.MouseDown(At(250), MouseButton.Right);
        window.MouseUp(At(250), MouseButton.Right);
        Assert.NotNull(lane.ContextMenu);
        var headers = lane.ContextMenu!.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToArray();
        Assert.Equal(["Fade in", "Fade out", "Loop", "Silence", "Reverse", "Bỏ hiệu ứng", "Xóa vùng chọn"], headers);
        window.Close();
    }

    [AvaloniaFact]
    public void RegionEffects_DriveEngine_LoopIsExclusive_AndLockBlocksEdits()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var a = vm.Tracks[0];
        var b = vm.Tracks[1];
        var ra = a.BeginRegion(1, 3);
        var rb = b.BeginRegion(5, 7);

        a.SetEffect(ra, RegionEffect.FadeIn);
        Assert.Equal("Fade in", ra.BadgeText);
        Assert.Equal(ClipEffectKind.FadeIn, Assert.Single(a.Strip.Effects).Kind);
        Assert.Null(vm.ActiveLoop);

        a.SetEffect(ra, RegionEffect.Loop);
        Assert.Empty(a.Strip.Effects);                 // Loop không phải hiệu ứng trên stem
        Assert.Equal(new LoopRegion(44100, 3 * 44100, 4), vm.ActiveLoop);
        Assert.Equal(1.0, vm.Timeline.PositionSeconds, 2); // playhead nhảy về đầu đoạn lặp

        // Đặt Loop ở vùng khác: vùng Loop cũ bị bỏ hiệu ứng.
        b.SetEffect(rb, RegionEffect.Loop);
        Assert.Equal(RegionEffect.None, ra.Effect);
        Assert.Equal(new LoopRegion(5 * 44100, 7 * 44100, 4), vm.ActiveLoop);

        // Bỏ hiệu ứng: hết lặp.
        b.SetEffect(rb, RegionEffect.None);
        Assert.Null(vm.ActiveLoop);

        // Track khóa: không đổi hiệu ứng được.
        a.IsLocked = true;
        a.SetEffect(ra, RegionEffect.Reverse);
        Assert.Equal(RegionEffect.None, ra.Effect);
        Assert.Empty(a.Strip.Effects);
        window.Close();
    }
    [AvaloniaFact]
    public void LockingTrack_DisablesItsControls_OnlyForThatTrack()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var headers = window.GetVisualDescendants().OfType<TrackHeaderView>().ToList();
        Assert.Equal(5, headers.Count);
        var first = headers.First(h => ReferenceEquals(h.DataContext, vm.Tracks[0]));
        var second = headers.First(h => ReferenceEquals(h.DataContext, vm.Tracks[1]));

        // Chưa khóa: mọi điều khiển bật.
        Assert.All(first.GetVisualDescendants().OfType<PanBar>(), s => Assert.True(s.IsEnabled));
        Assert.True(first.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);

        vm.Tracks[0].IsLocked = true;
        Dispatcher.UIThread.RunJobs();

        // Còn lại một PanBar là pan; volume là VolumeBar.
        var firstPans = first.GetVisualDescendants().OfType<PanBar>().ToList();
        Assert.Single(firstPans);
        Assert.All(firstPans, s => Assert.False(s.IsEnabled));
        Assert.False(first.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);
        Assert.False(first.GetVisualDescendants().OfType<TextBox>().Single().IsEnabled);
        Assert.All(second.GetVisualDescendants().OfType<PanBar>(), s => Assert.True(s.IsEnabled));
        Assert.True(second.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);

        vm.Tracks[0].IsLocked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.All(first.GetVisualDescendants().OfType<PanBar>(), s => Assert.True(s.IsEnabled));
        Assert.True(first.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void PlayButton_TogglesPlayState()
    {
        var window = CreateWindow(out var vm);
        Assert.False(vm.IsPlaying);

        // Chưa có track: Play không làm gì.
        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);

        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        vm.PlayPauseCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal("⏸", vm.PlayPauseText);

        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.Equal("▶", vm.PlayPauseText);
        window.Close();
    }

    /// <summary>
    /// Opt-in (SOUNDMASTER_AUDIO_TEST=1): chạy toàn bộ đường đi nút Play → engine → WaveOut thật
    /// và xác nhận engine tạo ra tín hiệu khác 0.
    /// </summary>
    [AvaloniaFact]
    public void FullPath_PlayButton_ProducesSignal_OnRealDevice()
    {
        if (Environment.GetEnvironmentVariable("SOUNDMASTER_AUDIO_TEST") != "1") return;

        var vm = new MainViewModel(enableAudioOutput: true) { MasterGainDb = -30 };
        vm.LoadDemoCommand.Execute(null);
        vm.PlayPauseCommand.Execute(null);

        var engine = (MixerEngine)typeof(MainViewModel)
            .GetField("_engine", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(vm)!;

        Thread.Sleep(1200);
        var (l, r) = engine.MasterMeter.ReadAndReset();
        long position = engine.PositionFrames;
        bool playing = engine.IsPlaying;
        vm.PlayPauseCommand.Execute(null);
        vm.Dispose();

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "soundmaster_fullpath_report.txt"),
            $"vmStatus='{vm.Status}' playing={playing} position={position} meterL={l:0.0000} meterR={r:0.0000}");

        Assert.True(playing);
        Assert.True(position > SoundMaster.Core.Audio.EngineFormat.SampleRate / 2, $"position={position}; status={vm.Status}");
        Assert.True(Math.Max(l, r) > 0.001f, $"meter L={l} R={r}; status={vm.Status}");
    }

    [AvaloniaFact]
    public void LockingMaster_DisablesMasterVolume()
    {
        var window = CreateWindow(out var vm);
        var master = window.GetVisualDescendants().OfType<VolumeBar>()
            .First(s => s.DataContext is MainViewModel);
        Assert.True(master.IsEnabled);

        vm.IsMasterLocked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.False(master.IsEnabled);
        Assert.False(vm.IsMasterEditable);
        window.Close();
    }

    [Fact]
    public void VolumeBar_Mapping_IsConsistent()
    {
        Assert.Equal(0.0, VolumeBar.DbToFraction(VolumeBar.MinDb), 9);
        Assert.Equal(1.0, VolumeBar.DbToFraction(VolumeBar.MaxDb), 9);
        Assert.Equal(60.0 / 66.0, VolumeBar.DbToFraction(0), 9);
        Assert.Equal(0.0, VolumeBar.DbToFraction(-500), 9); // cắt về phạm vi
        foreach (double db in new[] { -60.0, -33.3, -6.0, 0.0, 6.0 })
            Assert.Equal(db, VolumeBar.FractionToDb(VolumeBar.DbToFraction(db)), 9);
    }

    [AvaloniaFact]
    public void VolumeBar_ClickAndDrag_SetVolume_DoubleClickResets()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var bar = window.GetVisualDescendants().OfType<VolumeBar>()
            .First(b => ReferenceEquals(b.DataContext, vm.Tracks[0]));
        double w = bar.Bounds.Width, h = bar.Bounds.Height;
        Assert.True(w > 50);
        Assert.Equal(20, h);

        Point At(double fraction) => bar.TranslatePoint(new Point(w * fraction, h / 2), window)!.Value;

        // Bấm giữa thanh → giữa thang (-27 dB), và ViewModel nhận giá trị qua binding hai chiều.
        window.MouseDown(At(0.5), MouseButton.Left);
        window.MouseUp(At(0.5), MouseButton.Left);
        Assert.Equal(-27.0, vm.Tracks[0].GainDb, 1);
        Assert.Equal(-27.0, vm.Tracks[0].Strip.GainDb, 1); // đã tới engine

        // Kéo từ 20% sang 80%.
        window.MouseDown(At(0.2), MouseButton.Left);
        window.MouseMove(At(0.8));
        window.MouseUp(At(0.8), MouseButton.Left);
        Assert.Equal(VolumeBar.FractionToDb(0.8), vm.Tracks[0].GainDb, 1);

        // Kéo ra ngoài mép phải thì dừng ở mức tối đa.
        window.MouseDown(At(0.9), MouseButton.Left);
        window.MouseMove(new Point(window.Width - 1, At(0.9).Y));
        window.MouseUp(new Point(window.Width - 1, At(0.9).Y), MouseButton.Left);
        Assert.Equal(VolumeBar.MaxDb, vm.Tracks[0].GainDb, 1);

        // Double-click → về 0 dB.
        window.MouseDown(At(0.3), MouseButton.Left);
        window.MouseUp(At(0.3), MouseButton.Left);
        window.MouseDown(At(0.3), MouseButton.Left);
        window.MouseUp(At(0.3), MouseButton.Left);
        Assert.Equal(0.0, vm.Tracks[0].GainDb, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void LockedVolumeBar_IgnoresMouse()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        vm.Tracks[2].IsLocked = true;
        Dispatcher.UIThread.RunJobs();

        var bar = window.GetVisualDescendants().OfType<VolumeBar>()
            .First(b => ReferenceEquals(b.DataContext, vm.Tracks[2]));
        var p = bar.TranslatePoint(new Point(bar.Bounds.Width * 0.2, 10), window)!.Value;
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Assert.Equal(0.0, vm.Tracks[2].GainDb);
        window.Close();
    }

    [AvaloniaFact]
    public void Timeline_ZoomAndScroll_StayInRange()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var t = vm.Timeline;
        for (int i = 0; i < 30; i++) t.ZoomAround(1.25, 100);
        Assert.Equal(SoundMaster.App.ViewModels.TimelineViewModel.MaxPixelsPerSecond, t.PixelsPerSecond);
        t.OffsetSeconds = 1e9;
        Assert.True(t.OffsetSeconds <= t.ScrollMaximum + 1e-9);
        t.OffsetSeconds = -50;
        Assert.Equal(0, t.OffsetSeconds);
        window.Close();
    }

    private static void Await(Task task)
    {
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void PianoRoll_opens_transcribes_and_renders_notes()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var lead = vm.Tracks.First(t => t.Name == "Lead");
        Assert.False(lead.HasNotes);
        Await(vm.OpenPianoRollAsync(lead));

        Assert.True(vm.IsPianoRoll);
        Assert.True(lead.IsSelected);
        Assert.True(lead.HasNotes, vm.Status);

        var roll = window.GetVisualDescendants().OfType<PianoRollCanvas>().Single();
        var keys = window.GetVisualDescendants().OfType<PianoKeysView>().Single();
        Assert.True(roll.IsEffectivelyVisible);
        Assert.False(window.FindControl<Grid>("Workspace")!.IsVisible);
        Assert.Same(lead, roll.Track);

        // Khung nhìn được căn theo khoảng nốt: mọi nốt nằm trong vùng nhìn thấy.
        double visibleRows = roll.Bounds.Height / vm.RollRowHeight;
        Assert.All(lead.Notes, n =>
        {
            Assert.True(n.MidiNote + 1 <= vm.RollTopNote + 0.01, $"{n.Name} ngoài đỉnh");
            Assert.True(n.MidiNote >= vm.RollTopNote - visibleRows - 0.01, $"{n.Name} ngoài đáy");
        });

        // Chụp màn hình để xem.
        vm.Timeline.PositionSeconds = 4;
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(Path.GetTempPath(), "soundmaster_roll.png"));

        // Chuyển sang track khác chưa có nốt → tự bóc.
        var bass = vm.Tracks.First(t => t.Name == "Bass");
        Assert.False(bass.HasNotes);
        bass.RequestSelect();
        Assert.True(bass.IsTranscribing);
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (bass.IsTranscribing && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(bass.HasNotes, vm.Status);
        Assert.Same(bass, roll.Track);

        // Esc quay về mixer.
        window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsPianoRoll);
        Assert.True(window.FindControl<Grid>("Workspace")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Double_click_on_track_header_opens_piano_roll_and_transcribes()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var lead = vm.Tracks.First(t => t.Name == "Lead");
        var header = window.GetVisualDescendants().OfType<TrackHeaderView>().First(h => ReferenceEquals(h.DataContext, lead));
        var p = header.TranslatePoint(new Point(2, 30), window)!.Value; // vạch màu bên trái, không phải điều khiển

        window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        window.MouseUp(p, Avalonia.Input.MouseButton.Left);
        Assert.False(vm.IsPianoRoll);
        window.MouseDown(p, Avalonia.Input.MouseButton.Left);
        window.MouseUp(p, Avalonia.Input.MouseButton.Left);

        Assert.True(vm.IsPianoRoll);
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (lead.IsTranscribing || (!lead.HasNotes && DateTime.UtcNow < timeout))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(lead.HasNotes, vm.Status);
        window.Close();
    }

    [AvaloniaFact]
    public void PianoRoll_mouse_drag_moves_and_resizes_notes_and_double_click_adds()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        var lead = vm.Tracks.First(t => t.Name == "Lead");
        vm.Timeline.Bpm = 120;
        vm.GridIndex = 1; // 1/8 = 0,25 s
        lead.Notes =
        [
            new(1.0, 2.0, 64, 100),
            new(2.5, 3.0, 67, 90, 0.2),
            new(3.0, 3.5, 60, 90),
            new(4.0, 4.6, 65, 90),
        ];
        lead.RequestSelect();
        vm.IsPianoRoll = true;
        Dispatcher.UIThread.RunJobs();

        var roll = window.GetVisualDescendants().OfType<PianoRollCanvas>().Single();
        double pps = vm.Timeline.PixelsPerSecond;
        double rh = vm.RollRowHeight;
        Point P(double seconds, int midi) => roll.TranslatePoint(
            new Point((seconds - vm.Timeline.OffsetSeconds) * pps, (vm.RollTopNote - midi - 0.5) * rh), window)!.Value;

        // Kéo thân nốt đầu sang phải 1 s và lên 3 nửa cung.
        var n0 = lead.Notes[0];
        window.MouseDown(P(1.5, 64), MouseButton.Left);
        window.MouseMove(P(1.8, 65));
        window.MouseMove(P(2.5, 67));
        window.MouseUp(P(2.5, 67), MouseButton.Left);
        var moved = lead.Notes.Single(n => n.MidiNote == 67 && n.Duration > 0.9);
        Assert.Equal(2.0, moved.StartSeconds, 1);
        Assert.Equal(1.0, moved.Duration, 2);
        Assert.Same(moved, vm.SelectedNote);

        // Kéo cạnh phải của nốt cuối (4.0–4.6) dài thêm: bám lưới 0,25 s.
        var last = lead.Notes.Single(n => n.MidiNote == 65);
        double edgeX = (last.EndSeconds - vm.Timeline.OffsetSeconds) * pps - 2;
        var edge = roll.TranslatePoint(new Point(edgeX, (vm.RollTopNote - 65 - 0.5) * rh), window)!.Value;
        window.MouseDown(edge, MouseButton.Left);
        window.MouseMove(edge + new Vector(pps * 0.4, 0));
        window.MouseMove(edge + new Vector(pps * 0.9, 0));
        window.MouseUp(edge + new Vector(pps * 0.9, 0), MouseButton.Left);
        var resized = lead.Notes.Single(n => n.MidiNote == 65);
        Assert.Equal(4.0, resized.StartSeconds, 6);
        Assert.True(resized.EndSeconds > 5.0 && resized.EndSeconds < 5.7, $"end={resized.EndSeconds}");
        Assert.Equal(0, resized.EndSeconds / 0.25 - Math.Round(resized.EndSeconds / 0.25), 6);

        // Đúp-click chỗ trống: thêm nốt.
        int before = lead.Notes.Count;
        var empty = P(6.0, 66);
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        window.MouseDown(empty, MouseButton.Left);
        window.MouseUp(empty, MouseButton.Left);
        Assert.Equal(before + 1, lead.Notes.Count);
        Assert.Contains(lead.Notes, n => n.MidiNote == 66);

        var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Path.GetTempPath(), "soundmaster_roll2.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void PianoRoll_delete_doubtful_and_selected_notes()
    {
        var window = CreateWindow(out var vm);
        vm.LoadDemoCommand.Execute(null);
        var lead = vm.Tracks.First(t => t.Name == "Lead");
        lead.Notes =
        [
            new(0.0, 0.5, 60, 100, 0.9),
            new(0.5, 0.56, 64, 40, 0.2),
            new(1.0, 1.5, 67, 90, 0.8),
        ];
        lead.RequestSelect();
        Assert.Equal(1, lead.DoubtfulCount);

        vm.SelectedNote = lead.Notes[2];
        vm.DeleteSelectedNote();
        Assert.Equal(2, lead.Notes.Count);
        Assert.Null(vm.SelectedNote);

        vm.DeleteDoubtfulNotesCommand.Execute(null);
        Assert.Single(lead.Notes);
        Assert.Equal(60, lead.Notes[0].MidiNote);
        window.Close();
    }
}
