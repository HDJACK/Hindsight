using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class TopCulpritsTests
{
    private static List<Tick> Ticks()
    {
        var t1 = Tick(100, cpu: 100, S(0, cpu: 75, io: 300, net: 0, disk: 200, ws: 100), S(1, cpu: 25, io: 100, net: 50, disk: 600, ws: 300));
        t1.DiskBytes = 400; t1.NetBytes = 50; t1.GpuTotalPercent = 40; t1.Samples[0].GpuPercent = 40;
        var t2 = Tick(101, cpu: 50, S(0, cpu: 50, ws: 100));
        t2.GpuTotalPercent = 0;
        return new List<Tick> { t1, t2 };
    }

    [Fact]
    public void Rank_SharesAndScore()
    {
        var r = AsRecording(Ticks());
        var s = AppSettings.Default with { WeightCpu = 1, WeightGpu = 1, WeightIo = 1, WeightNet = 1, WeightMemory = 0 };
        var top = TopCulprits.Rank(r.Ticks, r, r.Meta.CoreCount, new TimeRange(100, 101), s, 10);
        Assert.Equal(2, top.Count);
        var a = top.Single(e => e.Totals.Name == "a.exe");
        // system cpu-seconds = (100+50)/100*4 = 6; a = (75+50)/100*4 = 5 → 5/6
        Assert.Equal(5.0 / 6, a.ShareCpu, 6);
        Assert.Equal(1.0, a.ShareGpu, 6);          // 40 of 40 GPU-seconds
        Assert.Equal(0.75, a.ShareIo, 6);          // 300 of 400
        Assert.Equal(0.25, a.ShareDisk, 6);         // 200 of 800 sample disk bytes
        Assert.Equal(0.0, a.ShareNet, 6);
        Assert.Equal(0.4, a.GpuSeconds, 6);
        // WeightIo covers both the file-I/O and the physical-disk share.
        Assert.Equal(5.0 / 6 + 1 + 0.75 + 0.25, a.Score, 6);
        var b = top.Single(e => e.Totals.Name == "b.exe");
        Assert.Equal(0.75, b.ShareDisk, 6);         // 600 of 800
        Assert.Equal(1.0, b.ShareNet, 6);
        Assert.Equal(0.75, b.ShareMemory, 6);      // 300 of 400 peak working set
        Assert.Equal("a.exe", top[0].Totals.Name);
    }

    [Fact]
    public void Rank_LimitsAndRange()
    {
        var r = AsRecording(Ticks());
        Assert.Single(TopCulprits.Rank(r.Ticks, r, 4, new TimeRange(100, 101), AppSettings.Default, 1));
        Assert.Single(TopCulprits.Rank(r.Ticks, r, 4, new TimeRange(101, 101), AppSettings.Default, 10));
        Assert.Empty(TopCulprits.Rank(r.Ticks, r, 4, new TimeRange(500, 600), AppSettings.Default, 10));
    }

    [Fact]
    public void Rank_ZeroSystemTotalsGiveZeroShares()
    {
        var r = AsRecording(new List<Tick> { Tick(100, cpu: 0, S(0)) });
        var e = TopCulprits.Rank(r.Ticks, r, 4, new TimeRange(100, 100), AppSettings.Default, 5).Single();
        Assert.Equal(0, e.ShareCpu); Assert.Equal(0, e.ShareIo); Assert.Equal(0, e.Score);
    }
}
