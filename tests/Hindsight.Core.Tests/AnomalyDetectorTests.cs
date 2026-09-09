using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class AnomalyDetectorTests
{
    private static readonly AppSettings S = AppSettings.Default with { BaselineWindowSeconds = 60, MinAnomalySeconds = 2 };

    [Fact]
    public void FlatLine_NoAnomalies()
    {
        Assert.Empty(AnomalyDetector.Detect(Flat(120), S));
    }

    [Fact]
    public void BaselinePath_FlagsSustainedRise()
    {
        var ticks = Flat(120, cpu: 10);
        for (int i = 80; i < 90; i++) ticks[i].CpuTotalPercent = 50;   // 5× baseline, above the 20 % floor, below the 80 % threshold
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.Cpu, a.Metric);
        Assert.Equal(1080, a.Range.FromUnix); Assert.Equal(1089, a.Range.ToUnix); Assert.Equal(10, a.Seconds);
        Assert.Equal(50, a.PeakValue); Assert.Equal(10, a.BaselineValue);
        Assert.Equal(Severity.High, a.Severity);   // 50/10 = 5 ≥ 4
    }

    [Fact]
    public void BaselinePath_RespectsNoiseFloorAndRatio()
    {
        var ticks = Flat(120, cpu: 2);
        for (int i = 80; i < 90; i++) ticks[i].CpuTotalPercent = 15;   // 7× baseline but below the 20 % floor
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
        var noisy = Flat(120, cpu: 40);
        for (int i = 0; i < 120; i += 2) noisy[i].CpuTotalPercent = 44;   // median 42, MAD 2
        for (int i = 80; i < 90; i++) noisy[i].CpuTotalPercent = 55;      // ≥ median + 4·MAD but < 1.5 × median
        Assert.Empty(AnomalyDetector.Detect(noisy, S));
    }

    [Fact]
    public void ThresholdPath_FlagsEvenOnHighBaseline()
    {
        var ticks = Flat(120, cpu: 70);
        for (int i = 30; i < 33; i++) ticks[i].CpuTotalPercent = 85;   // ≥ CpuSpikePercent 80
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Severity.Medium, a.Severity);   // ratio 1.2 → Low, lifted to Medium by the threshold
    }

    [Fact]
    public void Grouping_BridgesTwoTicksNotThree_AndGapEndsRun()
    {
        var ticks = Flat(120);
        foreach (int i in new[] { 40, 41, 44, 45 }) ticks[i].CpuTotalPercent = 60;          // bridge 2 → one anomaly 40..45
        foreach (int i in new[] { 70, 71, 75, 76 }) ticks[i].CpuTotalPercent = 60;          // bridge 3 → two anomalies
        foreach (int i in new[] { 100, 101, 103, 104 }) ticks[i].CpuTotalPercent = 60;
        ticks[102].Flags |= TickFlags.Gap;                                                   // gap splits
        var list = AnomalyDetector.Detect(ticks, S).OrderBy(a => a.Range.FromUnix).ToList();
        Assert.Equal(5, list.Count);
        Assert.Equal((1040L, 1045L), (list[0].Range.FromUnix, list[0].Range.ToUnix));
        Assert.Equal((1070L, 1071L), (list[1].Range.FromUnix, list[1].Range.ToUnix));
        Assert.Equal((1075L, 1076L), (list[2].Range.FromUnix, list[2].Range.ToUnix));
        Assert.Equal((1100L, 1101L), (list[3].Range.FromUnix, list[3].Range.ToUnix));
        Assert.Equal((1103L, 1104L), (list[4].Range.FromUnix, list[4].Range.ToUnix));
    }

    [Fact]
    public void MinDuration_DropsShortRunsUnlessThresholdReached()
    {
        var ticks = Flat(120);
        ticks[50].CpuTotalPercent = 60;    // 1 s, below threshold → dropped
        ticks[90].CpuTotalPercent = 95;    // 1 s, ≥ 80 → kept
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(1090, a.Range.FromUnix);
        Assert.Empty(AnomalyDetector.Detect(ticks, S with { MinAnomalySeconds = 2, CpuSpikePercent = 100 }));
    }

    [Fact]
    public void RamFree_UsesInvertedSemantics()
    {
        var ticks = Flat(120);
        for (int i = 60; i < 70; i++) ticks[i].RamAvailableBytes = 1L << 30;   // 8 GB → 1 GB
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.RamFree, a.Metric);
        Assert.Equal(1L << 30, a.PeakValue);
        Assert.Equal(Severity.High, a.Severity);    // baseline/peak = 8
        var small = Flat(120);
        for (int i = 60; i < 70; i++) small[i].RamAvailableBytes = (8L << 30) - (100L << 20);   // 100 MB drop < 256 MB floor
        Assert.Empty(AnomalyDetector.Detect(small, S));
    }

    [Fact]
    public void NoiseFloors_AreLowForNetworkAndIo()
    {
        Assert.Equal(256 * 1024, AnomalyDetector.NoiseFloor(Metric.Network));
        Assert.Equal(2 * 1_048_576, AnomalyDetector.NoiseFloor(Metric.FileIo));
        Assert.Equal(2 * 1_048_576, AnomalyDetector.NoiseFloor(Metric.DiskPhysical));

        // 512 KB/s over a 64 KB/s baseline: above the 256 KB/s network noise floor.
        var ticks = Flat(120);
        foreach (var t in ticks) t.NetBytes = 64 * 1024;
        for (int i = 60; i < 70; i++) ticks[i].NetBytes = 512 * 1024;
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.Network, a.Metric);
    }

    [Fact]
    public void CpuMhz_FlagsThrottlingWithInvertedRule()
    {
        Assert.Equal(RuleKind.Inverted, MetricInfo.Rule(Metric.CpuMhz));
        var ticks = Flat(120);
        foreach (var t in ticks) t.CpuMhz = 5000;
        for (int i = 60; i < 70; i++) ticks[i].CpuMhz = 1200;
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.CpuMhz, a.Metric);
        Assert.Equal(1200, a.PeakValue);
        Assert.Equal(5000, a.BaselineValue);
        Assert.Equal(10, a.Seconds);
        Assert.Equal(Severity.High, a.Severity);   // 5000/1200 = 4.2 >= 4
    }

    [Fact]
    public void CpuMhz_SmallDipIsBelowTheFloor()
    {
        var ticks = Flat(120);
        foreach (var t in ticks) t.CpuMhz = 1000;
        for (int i = 60; i < 70; i++) ticks[i].CpuMhz = 780;   // 220 MHz drop (22 %) < 300 MHz floor
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
    }

    [Fact]
    public void CpuMhz_SmallDipIsBelowTheRatioGate()
    {
        var ticks = Flat(120);
        foreach (var t in ticks) t.CpuMhz = 5000;
        for (int i = 60; i < 70; i++) ticks[i].CpuMhz = 4800;   // 200 MHz drop, ratio 0.96 > 0.8 gate
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
    }

    [Fact]
    public void CpuMhz_ZeroReadingsAreIgnoredNotTreatedAsThrottling()
    {
        var ticks = Flat(120);
        foreach (var t in ticks) t.CpuMhz = 5000;
        ticks[30].CpuMhz = 0;
        ticks[31].CpuMhz = 0;
        ticks[32].CpuMhz = 0;
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
    }

    [Fact]
    public void ProcessCount_UsesDeltaRuleNotRatio()
    {
        Assert.Equal(RuleKind.Delta, MetricInfo.Rule(Metric.ProcessCount));
        var ticks = Flat(120);
        foreach (var t in ticks) t.ProcessCount = 300;
        for (int i = 60; i < 65; i++) ticks[i].ProcessCount = 360;
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.ProcessCount, a.Metric);
        Assert.Equal(360, a.PeakValue);
        Assert.Equal(300, a.BaselineValue);
        Assert.Equal(5, a.Seconds);
        Assert.Equal(Severity.Medium, a.Severity);   // (360 - 300) / 20 = 3
    }

    [Fact]
    public void ProcessCount_SmallRiseIsBelowTheFloor()
    {
        var ticks = Flat(120);
        foreach (var t in ticks) t.ProcessCount = 300;
        for (int i = 60; i < 65; i++) ticks[i].ProcessCount = 310;   // +10 < 20 floor
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
    }

    [Fact]
    public void GpuMemory_FlagsJumpWithDeltaRule()
    {
        Assert.Equal(RuleKind.Delta, MetricInfo.Rule(Metric.GpuMemory));
        var ticks = Flat(120);
        foreach (var t in ticks) t.Samples[0].GpuMemoryBytes = 1L << 30;
        for (int i = 60; i < 70; i++) ticks[i].Samples[0].GpuMemoryBytes = 2560L << 20;   // 2.5 GB
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(Metric.GpuMemory, a.Metric);
        Assert.Equal(2560L << 20, a.PeakValue);
        Assert.Equal(1L << 30, a.BaselineValue);
        Assert.Equal(Severity.Medium, a.Severity);   // 1.5 GB / 512 MB = 3
    }

    [Fact]
    public void UnrecordedMetricsAreSkipped_AndCommitUsesThresholdOnly()
    {
        var ticks = Flat(120);
        for (int i = 60; i < 70; i++) ticks[i].CommitPercent = 60;    // huge relative rise, below 90 → nothing
        Assert.Empty(AnomalyDetector.Detect(ticks, S));
        for (int i = 60; i < 70; i++) ticks[i].CommitPercent = 95;
        Assert.Equal(Metric.Commit, Assert.Single(AnomalyDetector.Detect(ticks, S)).Metric);
    }

    [Fact]
    public void Sensitivity_ChangesK()
    {
        // Alternating 28/36 → for the block covering ticks 80..83 the baseline is median 32, MAD 4:
        // Normal (k 4) cut-off 48, Low (k 6) 56, High (k 3) 44, and the ratio floor is 1.5 × 32 = 48.
        var ticks = Flat(120, cpu: 28);
        for (int i = 0; i < 120; i += 2) ticks[i].CpuTotalPercent = 36;
        for (int i = 80; i < 90; i++) ticks[i].CpuTotalPercent = 50;
        // The run stops at tick 83, not 89: the next block (84..89) takes its baseline from a window that already
        // contains the four hot ticks, so its median rises to 36 (MAD 8) and 50 no longer clears median + k·MAD.
        var normal = Assert.Single(AnomalyDetector.Detect(ticks, S with { AnomalySensitivity = AnomalySensitivity.Normal }));
        Assert.Equal((1080L, 1083L), (normal.Range.FromUnix, normal.Range.ToUnix));
        Assert.Empty(AnomalyDetector.Detect(ticks, S with { AnomalySensitivity = AnomalySensitivity.Low }));
        Assert.Single(AnomalyDetector.Detect(ticks, S with { AnomalySensitivity = AnomalySensitivity.High }));
    }

    /// <summary>With a 2 s sample interval the run's duration is the summed interval (20 s over ten ticks), not the
    /// unix span (19 s); the same fixture pins RangeSummary seconds and TopCulprits CPU-seconds to interval seconds.</summary>
    [Fact]
    public void IntervalTwo_DurationRangeSummaryAndCpuSecondsUseIntervalSeconds()
    {
        var ticks = Flat(120, cpu: 10, intervalSeconds: 2);
        for (int i = 80; i < 90; i++) ticks[i].CpuTotalPercent = 50;
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(1160, a.Range.FromUnix); Assert.Equal(1178, a.Range.ToUnix);
        Assert.Equal(19, a.Range.Seconds);          // unix span
        Assert.Equal(20, a.DurationSeconds);        // 10 ticks x 2 s
        Assert.Equal(20, a.Seconds);
        Assert.Contains("for 20 s", ExplanationBuilder.Sentence(a, Array.Empty<Contributor>(), null, false, Array.Empty<Marker>()));

        var r = AsRecording(ticks);
        var full = new TimeRange(ticks[0].UnixTime, ticks[^1].UnixTime);
        Assert.Equal(240, RangeSummary.System(ticks, full).Seconds);   // 120 ticks x 2 s

        var top = TopCulprits.Rank(ticks, r, r.Meta.CoreCount, full, AppSettings.Default, 5).Single();
        Assert.Equal(48.0, top.Totals.CpuSeconds, 6);                  // 5 % x 2 s x 4 cores x 120 ticks
        Assert.Equal(240, top.Totals.Seconds);
    }

    /// <summary>A zero baseline carries no ratio information, so it classifies as Low; only an absolute-threshold
    /// hit lifts it, and then only to Medium.</summary>
    [Fact]
    public void Classify_ZeroBaselineIsLowUnlessThresholdLiftsItToMedium()
    {
        Assert.Equal(Severity.Low, AnomalyDetector.Classify(Metric.Cpu, 50, 0, 80));
        Assert.Equal(Severity.Medium, AnomalyDetector.Classify(Metric.Cpu, 90, 0, 80));
        Assert.Equal(Severity.Low, AnomalyDetector.Classify(Metric.Cpu, 90, 0, null));
        // Lower-is-worse: free memory at zero against a real baseline is still the worst case.
        Assert.Equal(Severity.High, AnomalyDetector.Classify(Metric.RamFree, 0, 8L << 30, null));
        Assert.Equal(Severity.Low, AnomalyDetector.Classify(Metric.RamFree, 0, 0, null));

        // End to end: an idle machine (baseline 0) that jumps over the CPU threshold reads Medium, not High.
        var ticks = Flat(120, cpu: 0);
        for (int i = 80; i < 90; i++) ticks[i].CpuTotalPercent = 90;
        var a = Assert.Single(AnomalyDetector.Detect(ticks, S));
        Assert.Equal(0, a.BaselineValue);
        Assert.Equal(Severity.Medium, a.Severity);
    }

    [Fact]
    public void Sorted_BySeverityThenTime()
    {
        var ticks = Flat(200);
        for (int i = 20; i < 25; i++) ticks[i].CpuTotalPercent = 25;    // ratio 2.5 → Medium
        for (int i = 100; i < 105; i++) ticks[i].CpuTotalPercent = 60;  // ratio 6 → High
        var list = AnomalyDetector.Detect(ticks, S);
        Assert.Equal(Severity.High, list[0].Severity); Assert.Equal(1100, list[0].Range.FromUnix);
        Assert.Equal(Severity.Medium, list[1].Severity);
    }

    [Fact]
    public void Detect_LargeRecording_IsFast()
    {
        var ticks = Flat(10_800);
        var rnd = new Random(1);
        foreach (var t in ticks) { t.CpuTotalPercent = 10 + rnd.Next(5); t.GpuTotalPercent = 5; t.DiskLatencyMs = 1; t.CommitPercent = 40; t.NetBytes = rnd.Next(1000); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AnomalyDetector.Detect(ticks, AppSettings.Default);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
