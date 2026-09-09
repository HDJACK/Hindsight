using System.Globalization;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;

namespace Hindsight.App.Controls;

/// <summary>Everything <see cref="AnalysisTabs.SetSource(AnalysisSource)"/> binds for a whole source, pre-built so the
/// control only assigns item sources. <see cref="Build"/> is pure data work (no WPF types are touched), which is what
/// lets <see cref="Services.AnalysisRunner"/> run it on the thread pool together with detection.</summary>
public sealed record AnalysisSource(
    AnalysisContext Context,
    IReadOnlyDictionary<int, ProcessTotals> Totals,
    IReadOnlyList<SummaryItem> Summary,
    IReadOnlyList<ShareRow> Rows,
    IReadOnlyList<ProcessPick> Picks)
{
    /// <summary>The empty source: no ticks, no anomalies, nothing to show.</summary>
    public static AnalysisSource Empty(AnalysisContext ctx) => new(
        ctx, new Dictionary<int, ProcessTotals>(), Array.Empty<SummaryItem>(), Array.Empty<ShareRow>(), Array.Empty<ProcessPick>());

    /// <summary>Aggregates the whole source once: one <see cref="RecordingAggregates.PerProcess"/> pass feeds both the
    /// totals lookup and the culprit ranking, and the summary tiles come from a single <see cref="RangeSummary"/> pass.</summary>
    public static AnalysisSource Build(AnalysisContext ctx, AppSettings settings)
    {
        var ticks = ctx.Ticks;
        if (ticks.Count == 0) return Empty(ctx);
        var full = new TimeRange(ticks[0].UnixTime, ticks[^1].UnixTime);

        var perProcess = RecordingAggregates.PerProcess(ticks, ctx.Ids, ctx.CoreCount, full.FromUnix, full.ToUnix);
        // A loop instead of ToDictionary so a duplicate identity id (should not happen, but is not guaranteed by the
        // source) does not throw; last one wins.
        var totals = new Dictionary<int, ProcessTotals>(perProcess.Count);
        foreach (var p in perProcess) totals[p.IdentityId] = p;

        var picks = perProcess
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid)
            .Select(p => new ProcessPick(p.IdentityId, $"{p.Name} ({p.Pid})", p.Name))
            .ToList();

        return new AnalysisSource(
            ctx, totals,
            BuildSummary(ticks, full, ctx.Anomalies),
            BuildShareRows(ctx, perProcess, full, settings),
            picks);
    }

    internal static IReadOnlyList<SummaryItem> BuildSummary(IReadOnlyList<Tick> ticks, TimeRange range, IReadOnlyList<Anomaly> anomalies)
    {
        var s = RangeSummary.System(ticks, range);
        var ram = s.Stat(Metric.RamFree);
        return new List<SummaryItem>
        {
            AvgPeak(s, ticks, range, Metric.Cpu),
            AvgPeak(s, ticks, range, Metric.Gpu),
            Throughput(s, ticks, range, Metric.FileIo),
            Throughput(s, ticks, range, Metric.DiskPhysical),
            Throughput(s, ticks, range, Metric.Network),
            // RamFree is LowerIsWorse, so Peak holds the minimum over the range.
            new("RAM free", MetricInfo.Format(Metric.RamFree, ram.Peak), "avg " + MetricInfo.Format(Metric.RamFree, ram.Avg)),
            AvgPeak(s, ticks, range, Metric.DiskLatency),
            AvgPeak(s, ticks, range, Metric.Commit),
            new("Gaps", Format.Duration(s.GapSeconds), $"of {Format.Duration(s.Seconds + s.GapSeconds)} total"),
            new("Anomalies", anomalies.Count.ToString(CultureInfo.InvariantCulture), SeverityCounts(anomalies)),
        };
    }

    /// <summary>Ranks the culprits over <paramref name="range"/>, computing the per-process totals itself.</summary>
    internal static IReadOnlyList<ShareRow> BuildShareRows(AnalysisContext ctx, TimeRange range, AppSettings settings) =>
        BuildShareRows(ctx, RecordingAggregates.PerProcess(ctx.Ticks, ctx.Ids, ctx.CoreCount, range.FromUnix, range.ToUnix), range, settings);

    private static IReadOnlyList<ShareRow> BuildShareRows(AnalysisContext ctx, IReadOnlyList<ProcessTotals> perProcess, TimeRange range, AppSettings settings)
    {
        var ranked = TopCulprits.Rank(ctx.Ticks, perProcess, ctx.CoreCount, range, settings, AnalysisTabs.TopN);
        var rows = new List<ShareRow>(ranked.Count);
        foreach (var e in ranked)
        {
            var t = e.Totals;
            double cpu = e.ShareCpu * 100, gpu = e.ShareGpu * 100, io = e.ShareIo * 100, disk = e.ShareDisk * 100, net = e.ShareNet * 100;
            rows.Add(new ShareRow(
                t.IdentityId, $"{t.Name} ({t.Pid})",
                cpu, gpu, io, disk, net,
                Format.Percent(cpu), Format.Percent(gpu), Format.Percent(io), Format.Percent(disk), Format.Percent(net),
                e.Score.ToString("0.00", CultureInfo.InvariantCulture), Format.Duration(t.Seconds),
                e.Score, t.Seconds));
        }
        return rows;
    }

    private static SummaryItem AvgPeak(SystemSummary s, IReadOnlyList<Tick> ticks, TimeRange range, Metric m)
    {
        if (!IsRecordedInRange(ticks, range, m)) return new SummaryItem(MetricInfo.Label(m), "not recorded", "");
        var st = s.Stat(m);
        return new SummaryItem(MetricInfo.Label(m), MetricInfo.Format(m, st.Avg), "peak " + MetricInfo.Format(m, st.Peak));
    }

    private static SummaryItem Throughput(SystemSummary s, IReadOnlyList<Tick> ticks, TimeRange range, Metric m)
    {
        if (!IsRecordedInRange(ticks, range, m)) return new SummaryItem(MetricInfo.Label(m), "not recorded", "");
        var st = s.Stat(m);
        return new SummaryItem(MetricInfo.Label(m), MetricInfo.Format(m, st.Avg),
            $"peak {MetricInfo.Format(m, st.Peak)} · total {MetricInfo.Bytes(st.Total)}");
    }

    /// <summary>Like <see cref="MetricSeries.IsRecorded"/> but evaluated only over the ticks inside <paramref name="range"/>,
    /// so a signal that starts recording partway through the recording reads "not recorded" for a selection before it began.</summary>
    private static bool IsRecordedInRange(IReadOnlyList<Tick> ticks, TimeRange range, Metric m)
    {
        if (m is Metric.Cpu or Metric.FileIo or Metric.Network or Metric.RamFree) return true;
        foreach (var t in ticks)
        {
            if (!range.Contains(t.UnixTime)) continue;
            if (t.Has(TickFlags.Gap)) continue;
            if (MetricSeries.Value(t, m) != 0) return true;
        }
        return false;
    }

    private static string SeverityCounts(IReadOnlyList<Anomaly> anomalies)
    {
        if (anomalies.Count == 0) return "none";
        int high = 0, medium = 0, low = 0;
        foreach (var a in anomalies)
        {
            if (a.Severity == Severity.High) high++;
            else if (a.Severity == Severity.Medium) medium++;
            else low++;
        }
        var parts = new List<string>(3);
        if (high > 0) parts.Add($"{high} high");
        if (medium > 0) parts.Add($"{medium} medium");
        if (low > 0) parts.Add($"{low} low");
        return string.Join(" · ", parts);
    }
}
