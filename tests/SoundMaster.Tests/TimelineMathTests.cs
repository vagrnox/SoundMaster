using SoundMaster.Core.Audio;
using SoundMaster.Core.Timeline;

namespace SoundMaster.Tests;

public class TimelineMathTests
{
    [Theory]
    [InlineData(0, "00:00.000")]
    [InlineData(75.5, "01:15.500")]
    [InlineData(-3, "00:00.000")]
    public void Clock_Formats(double seconds, string expected)
        => Assert.Equal(expected, TimelineMath.Clock(seconds));

    [Fact]
    public void Bars_AtOrigin_IsOneOneOne()
        => Assert.Equal("001.1.1", TimelineMath.Bars(0, 120));

    [Fact]
    public void Bars_AdvancesWithTempo()
    {
        // 120 BPM: 1 beat = 0.5 s, 1 bar (4/4) = 2 s.
        Assert.Equal("002.1.1", TimelineMath.Bars(2.0, 120));
        Assert.Equal("001.2.1", TimelineMath.Bars(0.5, 120));
        Assert.Equal("001.1.2", TimelineMath.Bars(0.125, 120));
    }

    [Fact]
    public void NiceTimeStep_GrowsAsZoomOut()
    {
        double zoomedIn = TimelineMath.NiceTimeStep(400);
        double zoomedOut = TimelineMath.NiceTimeStep(2);
        Assert.True(zoomedIn < zoomedOut);
        Assert.True(zoomedOut * 2 >= 80);
    }

    [Fact]
    public void RulerLabel_ShowsTenthsOnlyBelowOneSecond()
    {
        Assert.Equal("00:15", TimelineMath.RulerLabel(15, 5));
        Assert.Equal("01:05.5", TimelineMath.RulerLabel(65.5, 0.5));
    }

    [Fact]
    public void GridLines_OnePerBar_WhenBeatsTooNarrow()
    {
        // 120 BPM, 4/4: 1 ô nhịp = 2 s = 40 px ở 20 px/s; phách 10 px (< 24) nên không có vạch phách.
        var lines = TimelineMath.GridLines(0, 20, 200, 120, 4);
        Assert.All(lines, l => Assert.True(l.IsBar));
        Assert.Equal(0, lines[0].X, 6);
        Assert.Equal(40, lines[1].X, 6);
        Assert.Equal(80, lines[2].X, 6);
    }

    [Fact]
    public void GridLines_AddsBeatLines_WhenZoomedIn()
    {
        // 100 px/s: ô nhịp 200 px, phách 50 px → 3 vạch phách giữa hai vạch ô nhịp.
        var lines = TimelineMath.GridLines(0, 100, 250, 120, 4);
        Assert.Equal([0.0, 50.0, 100.0, 150.0, 200.0],
            lines.Where(l => l.X <= 200).Select(l => l.X).Order().ToArray());
        Assert.Equal(2, lines.Count(l => l.IsBar && l.X <= 200));
    }

    [Fact]
    public void GridLines_ThinsOut_WhenZoomedFarOut()
    {
        // 2 px/s: ô nhịp chỉ 4 px → chỉ vẽ mỗi 2 ô nhịp (8 px).
        var lines = TimelineMath.GridLines(0, 2, 100, 120, 4);
        Assert.All(lines, l => Assert.True(l.IsBar));
        Assert.Equal(8, lines[1].X - lines[0].X, 6);
    }

    [Fact]
    public void GridLines_FollowsScrollOffset()
    {
        // Cuộn 3 s: vạch ô nhịp thứ hai (t = 4 s) nằm ở x = (4 - 3) * 20 = 20.
        var lines = TimelineMath.GridLines(3, 20, 200, 120, 4);
        Assert.Contains(lines, l => l.IsBar && Math.Abs(l.X - 20) < 1e-6);
    }

    [Fact]
    public void GridLines_InvalidInput_ReturnsEmpty()
    {
        Assert.Empty(TimelineMath.GridLines(0, 0, 100, 120, 4));
        Assert.Empty(TimelineMath.GridLines(0, 20, 100, 0, 4));
    }

    [Fact]
    public void MaxPeak_ClampsRange()
    {
        float[] peaks = [0.1f, 0.9f, 0.3f];
        Assert.Equal(0.9f, TimelineMath.MaxPeak(peaks, -5, 100));
        Assert.Equal(0.3f, TimelineMath.MaxPeak(peaks, 2, 3));
        Assert.Equal(0f, TimelineMath.MaxPeak(peaks, 5, 9));
    }

    [Fact]
    public void AudioTrack_Peaks_MatchSignal()
    {
        int frames = AudioTrack.PeakBucketFrames * 3;
        var data = new float[frames * 2];
        // bucket 0 im lặng, bucket 1 biên độ 0.5 (chỉ kênh phải), bucket 2 biên độ 1.0
        for (int f = AudioTrack.PeakBucketFrames; f < 2 * AudioTrack.PeakBucketFrames; f++) data[2 * f + 1] = 0.5f;
        for (int f = 2 * AudioTrack.PeakBucketFrames; f < frames; f++) data[2 * f] = 1.0f;

        var track = AudioTrack.FromFloat("t", data);
        Assert.Equal(3, track.Peaks.Length);
        Assert.Equal(0f, track.Peaks[0]);
        Assert.InRange(track.Peaks[1], 0.499f, 0.501f);
        Assert.InRange(track.Peaks[2], 0.999f, 1f);
    }
}
