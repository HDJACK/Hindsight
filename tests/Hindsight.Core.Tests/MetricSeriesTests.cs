using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class MetricSeriesTests
{
    [Fact]
    public void Value_NormalisesRatesPerSecond()
    {
        var t = new Tick { UnixTime = 1, IntervalSeconds = 2, DiskBytes = 20, NetBytes = 10, CpuTotalPercent = 55, RamAvailableBytes = 7, DiskLatencyMs = 3.5f, CommitPercent = 61, HardFaultsPerSec = 9, GpuTotalPercent = 12,
            Samples = new[] { S(0, disk: 6), S(1, disk: 4) } };
        Assert.Equal(10, MetricSeries.Value(t, Metric.FileIo));
        Assert.Equal(5, MetricSeries.Value(t, Metric.Network));
        Assert.Equal(5, MetricSeries.Value(t, Metric.DiskPhysical));
        Assert.Equal(55, MetricSeries.Value(t, Metric.Cpu));
        Assert.Equal(12, MetricSeries.Value(t, Metric.Gpu));
        Assert.Equal(7, MetricSeries.Value(t, Metric.RamFree));
        Assert.Equal(3.5, MetricSeries.Value(t, Metric.DiskLatency), 3);
        Assert.Equal(61, MetricSeries.Value(t, Metric.Commit));
        Assert.Equal(9, MetricSeries.Value(t, Metric.HardFaults));
    }

    [Fact]
    public void Value_NewSignals()
    {
        var t = new Tick { UnixTime = 1, IntervalSeconds = 2, CpuMhz = 4200, ProcessCount = 317,
            Samples = new[] { S(0, gpuMem: 700L << 20), S(1, gpuMem: 324L << 20) } };
        Assert.Equal(4200, MetricSeries.Value(t, Metric.CpuMhz));       // not a rate: no /interval
        Assert.Equal(317, MetricSeries.Value(t, Metric.ProcessCount));
        Assert.Equal(1024L << 20, MetricSeries.Value(t, Metric.GpuMemory));

        var ticks = Flat(5);
        Assert.False(MetricSeries.IsRecorded(ticks, Metric.CpuMhz));
        Assert.False(MetricSeries.IsRecorded(ticks, Metric.ProcessCount));
        Assert.False(MetricSeries.IsRecorded(ticks, Metric.GpuMemory));
        ticks[2].CpuMhz = 3000;
        Assert.True(MetricSeries.IsRecorded(ticks, Metric.CpuMhz));
    }

    [Fact]
    public void Value_GapIsNaN()
    {
        var t = new Tick { UnixTime = 1, Flags = TickFlags.Gap, CpuTotalPercent = 50 };
        Assert.True(double.IsNaN(MetricSeries.Value(t, Metric.Cpu)));
        Assert.True(double.IsNaN(MetricSeries.Values(new[] { t }, Metric.Cpu)[0]));
    }

    [Fact]
    public void IsRecorded_FalseWhenSignalAlwaysZero()
    {
        var ticks = Flat(5);
        Assert.False(MetricSeries.IsRecorded(ticks, Metric.Gpu));
        Assert.False(MetricSeries.IsRecorded(ticks, Metric.DiskLatency));
        Assert.True(MetricSeries.IsRecorded(ticks, Metric.Cpu));
        Assert.True(MetricSeries.IsRecorded(ticks, Metric.RamFree));
        ticks[2].GpuTotalPercent = 1;
        Assert.True(MetricSeries.IsRecorded(ticks, Metric.Gpu));
    }

    [Fact]
    public void MetricInfo_FormatsAndLanes()
    {
        Assert.Equal("92.0 %", MetricInfo.Format(Metric.Cpu, 92));
        Assert.Equal("300 MB/s", MetricInfo.Format(Metric.FileIo, 300 * 1048576.0));
        Assert.Equal("12.5 ms", MetricInfo.Format(Metric.DiskLatency, 12.5));
        Assert.Equal("150 /s", MetricInfo.Format(Metric.HardFaults, 150));
        Assert.Equal("1.5 GB", MetricInfo.Format(Metric.RamFree, 1.5 * 1073741824));
        Assert.Equal(LaneKind.RamFree, MetricInfo.Lane(Metric.HardFaults));
        Assert.True(MetricInfo.LowerIsWorse(Metric.RamFree));
        Assert.Equal(12, MetricInfo.All.Count);
    }

    [Fact]
    public void MetricInfo_NewMetricsTable()
    {
        Assert.Equal("CPU MHz", MetricInfo.Label(Metric.CpuMhz));
        Assert.Equal("Processes", MetricInfo.Label(Metric.ProcessCount));
        Assert.Equal("GPU memory", MetricInfo.Label(Metric.GpuMemory));

        Assert.Equal(LaneKind.CpuMhz, MetricInfo.Lane(Metric.CpuMhz));
        Assert.Equal(LaneKind.Cpu, MetricInfo.Lane(Metric.ProcessCount));
        Assert.Equal(LaneKind.Gpu, MetricInfo.Lane(Metric.GpuMemory));

        Assert.Equal("4200 MHz", MetricInfo.Format(Metric.CpuMhz, 4200));
        Assert.Equal("317", MetricInfo.Format(Metric.ProcessCount, 317));
        Assert.Equal("2.5 GB", MetricInfo.Format(Metric.GpuMemory, 2560L << 20));

        Assert.Equal(RuleKind.Inverted, MetricInfo.Rule(Metric.CpuMhz));
        Assert.Equal(RuleKind.Delta, MetricInfo.Rule(Metric.ProcessCount));
        Assert.Equal(RuleKind.Delta, MetricInfo.Rule(Metric.GpuMemory));
        Assert.Equal(RuleKind.Inverted, MetricInfo.Rule(Metric.RamFree));
        Assert.Equal(RuleKind.ThresholdOnly, MetricInfo.Rule(Metric.Commit));
        Assert.Equal(RuleKind.Ratio, MetricInfo.Rule(Metric.Cpu));

        Assert.True(MetricInfo.LowerIsWorse(Metric.CpuMhz));
        Assert.False(MetricInfo.LowerIsWorse(Metric.ProcessCount));
        Assert.Equal(0.75, MetricInfo.InvertedRatio(Metric.RamFree));
        Assert.Equal(0.8, MetricInfo.InvertedRatio(Metric.CpuMhz));
    }

    [Fact]
    public void TimeRange_Basics()
    {
        var r = TimeRange.Of(10, 5);
        Assert.Equal(5, r.FromUnix); Assert.Equal(10, r.ToUnix); Assert.Equal(6, r.Seconds);
        Assert.True(r.Contains(10)); Assert.False(r.Contains(11));
    }
}
