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
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
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
        Assert.All(first.GetVisualDescendants().OfType<Slider>(), s => Assert.True(s.IsEnabled));
        Assert.True(first.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);

        vm.Tracks[0].IsLocked = true;
        Dispatcher.UIThread.RunJobs();

        // Còn lại một Slider là pan; volume là VolumeBar.
        var firstSliders = first.GetVisualDescendants().OfType<Slider>().ToList();
        Assert.Single(firstSliders);
        Assert.All(firstSliders, s => Assert.False(s.IsEnabled));
        Assert.False(first.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);
        Assert.False(first.GetVisualDescendants().OfType<TextBox>().Single().IsEnabled);
        Assert.All(second.GetVisualDescendants().OfType<Slider>(), s => Assert.True(s.IsEnabled));
        Assert.True(second.GetVisualDescendants().OfType<VolumeBar>().Single().IsEnabled);

        vm.Tracks[0].IsLocked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.All(first.GetVisualDescendants().OfType<Slider>(), s => Assert.True(s.IsEnabled));
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
}
