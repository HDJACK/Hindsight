using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class RangeSummaryTests
{
    [Fact]
    public void System_AveragesPeaksTotalsAndGaps()
    {
        var ticks = new List<Tick>
        {
            Tick(100, cpu: 20), Tick(101, cpu: 60), new() { UnixTime = 102, IntervalSeconds = 1, Flags = TickFlags.Gap }, Tick(103, cpu: 40),
        };
        ticks[0].DiskBytes = 100; ticks[1].DiskBytes = 300; ticks[3].DiskBytes = 200;
        ticks[0].RamAvailableBytes = 10; ticks[1].RamAvailableBytes = 4; ticks[3].RamAvailableBytes = 7;
        var s = RangeSummary.System(ticks, new TimeRange(100, 103));
        Assert.Equal(3, s.Seconds); Assert.Equal(1, s.GapSeconds);
        Assert.Equal(40, s.Stat(Metric.Cpu).Avg, 6); Assert.Equal(60, s.Stat(Metric.Cpu).Peak);
        Assert.Equal(200, s.Stat(Metric.FileIo).Avg, 6); Assert.Equal(300, s.Stat(Metric.FileIo).Peak); Assert.Equal(600, s.Stat(Metric.FileIo).Total);
        Assert.Equal(4, s.Stat(Metric.RamFree).Peak);   // min for RamFree
        Assert.Equal(7, s.Stat(Metric.RamFree).Avg, 6);
    }

    [Fact]
    public void System_RangeFilterAndEmpty()
    {
        var ticks = Flat(10);
        Assert.Equal(3, RangeSummary.System(ticks, new TimeRange(1002, 1004)).Seconds);
        var empty = RangeSummary.System(ticks, new TimeRange(5000, 5010));
        Assert.Equal(0, empty.Seconds); Assert.Equal(0, empty.Stat(Metric.Cpu).Peak);
    }
}
