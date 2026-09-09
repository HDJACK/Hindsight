using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Tests;

public class ConfigurableAnalysisTests
{
    [Fact]
    public void Spike_CpuAtThreshold_IsSpike()
    {
        var d = new ThresholdSpikeDetector(() => AppSettings.Default with { CpuSpikePercent = 60 });
        Assert.True(d.IsSpike(new Tick { CpuTotalPercent = 60 }, ReadOnlySpan<Tick>.Empty));
        Assert.False(d.IsSpike(new Tick { CpuTotalPercent = 59.9f }, ReadOnlySpan<Tick>.Empty));
    }

    [Fact]
    public void Spike_IoAndNet_UseRatesPerSecond()
    {
        var d = new ThresholdSpikeDetector(() => AppSettings.Default with { FileIoSpikeMBps = 10, NetworkSpikeMBps = 1 });
        Assert.True(d.IsSpike(new Tick { DiskBytes = 10L << 20, IntervalSeconds = 1 }, ReadOnlySpan<Tick>.Empty));
        Assert.False(d.IsSpike(new Tick { DiskBytes = 10L << 20, IntervalSeconds = 2 }, ReadOnlySpan<Tick>.Empty));
        Assert.True(d.IsSpike(new Tick { NetBytes = 2L << 20, IntervalSeconds = 2 }, ReadOnlySpan<Tick>.Empty));
    }

    [Fact]
    public void Spike_ReadsSettingsLive()
    {
        var s = AppSettings.Default with { CpuSpikePercent = 90 };
        var d = new ThresholdSpikeDetector(() => s);
        var t = new Tick { CpuTotalPercent = 85 };
        Assert.False(d.IsSpike(t, ReadOnlySpan<Tick>.Empty));
        s = s with { CpuSpikePercent = 80 };
        Assert.True(d.IsSpike(t, ReadOnlySpan<Tick>.Empty));
    }

    [Fact]
    public void Spike_GpuLatencyCommit()
    {
        var d = new ThresholdSpikeDetector(() => AppSettings.Default with { GpuSpikePercent = 90, DiskLatencySpikeMs = 50, CommitSpikePercent = 90 });
        Assert.True(d.IsSpike(new Tick { GpuTotalPercent = 90 }, ReadOnlySpan<Tick>.Empty));
        Assert.True(d.IsSpike(new Tick { DiskLatencyMs = 50 }, ReadOnlySpan<Tick>.Empty));
        Assert.True(d.IsSpike(new Tick { CommitPercent = 90 }, ReadOnlySpan<Tick>.Empty));
        Assert.False(d.IsSpike(new Tick { GpuTotalPercent = 89, DiskLatencyMs = 49, CommitPercent = 89 }, ReadOnlySpan<Tick>.Empty));
    }

    [Fact]
    public void Ranker_IncludesGpuWeight()
    {
        var r = new WeightedProcessRanker(() => AppSettings.Default with { WeightCpu = 0, WeightIo = 0, WeightNet = 0, WeightMemory = 0, WeightGpu = 2 });
        var totals = new SystemTotals(50, 1, 10);
        var s = new ProcessSample { GpuPercent = 10 };
        Assert.Equal(20, r.Score(in s, in totals), 6);
    }

    [Fact]
    public void Ranker_ScoresSharesOfSystemTotals()
    {
        var r = new WeightedProcessRanker(() => AppSettings.Default with { WeightCpu = 2, WeightIo = 1, WeightNet = 1, WeightMemory = 1 });
        var totals = new SystemTotals(50, 1, 10, DiskBytes: 400, NetBytes: 200, RamTotalBytes: 1000);
        var s = new ProcessSample { CpuPercent = 10, ReadBytes = 100, WriteBytes = 100, NetBytes = 50, WorkingSet = 250 };
        Assert.Equal(2 * 10 + 50 + 25 + 25, r.Score(in s, in totals), 6);
    }

    [Fact]
    public void Ranker_ZeroTotals_NoDivisionByZero()
    {
        var r = new WeightedProcessRanker(() => AppSettings.Default);
        var totals = new SystemTotals(50, 1, 10);
        var s = new ProcessSample { CpuPercent = 5, ReadBytes = 100 };
        Assert.Equal(5, r.Score(in s, in totals), 6);
    }

    [Fact]
    public void Ranker_ZeroWeights_ScoreZero()
    {
        var r = new WeightedProcessRanker(() => AppSettings.Default with { WeightCpu = 0, WeightIo = 0, WeightNet = 0, WeightMemory = 0 });
        var totals = new SystemTotals(50, 1, 10);
        var s = new ProcessSample { CpuPercent = 99, ReadBytes = 1 << 30 };
        Assert.Equal(0, r.Score(in s, in totals));
    }
}
