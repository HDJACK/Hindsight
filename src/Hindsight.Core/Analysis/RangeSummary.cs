using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>Average, peak and, where it means anything, cumulative total of one metric.</summary>
public sealed record MetricStat(double Avg, double Peak, double Total);

/// <summary>System-wide statistics for a time range, including how much of it was lost to gaps.</summary>
public sealed record SystemSummary(int Seconds, int GapSeconds, IReadOnlyDictionary<Metric, MetricStat> Stats)
{
    public MetricStat Stat(Metric m) => Stats.TryGetValue(m, out var s) ? s : new MetricStat(0, 0, 0);
}

/// <summary>Summarises the system-wide metrics over a time range.</summary>
public static class RangeSummary
{
    /// <summary>Averages/peaks/totals over non-gap ticks in range. Seconds = Σ interval of non-gap ticks; GapSeconds = Σ interval of gap ticks. Empty range → all zeros.</summary>
    public static SystemSummary System(IReadOnlyList<Tick> ticks, TimeRange range)
    {
        int seconds = 0, gapSeconds = 0;
        var sums = new Dictionary<Metric, double>();
        var counts = new Dictionary<Metric, int>();
        var peaks = new Dictionary<Metric, double>();
        var totals = new Dictionary<Metric, double>();
        foreach (var m in MetricInfo.All) { sums[m] = 0; counts[m] = 0; peaks[m] = double.NaN; totals[m] = 0; }

        foreach (var t in ticks)
        {
            if (!range.Contains(t.UnixTime)) continue;
            int interval = Math.Max(1, (int)t.IntervalSeconds);
            if (t.Has(TickFlags.Gap)) { gapSeconds += interval; continue; }
            seconds += interval;

            foreach (var m in MetricInfo.All)
            {
                double v = MetricSeries.Value(t, m);
                sums[m] += v;
                counts[m]++;
                bool lowerIsWorse = MetricInfo.LowerIsWorse(m);
                if (double.IsNaN(peaks[m]) || (lowerIsWorse ? v < peaks[m] : v > peaks[m])) peaks[m] = v;

                totals[m] += m switch
                {
                    Metric.FileIo => t.DiskBytes,
                    Metric.Network => t.NetBytes,
                    Metric.DiskPhysical => MetricSeries.SumDiskPhysical(t),
                    _ => 0,
                };
            }
        }

        var stats = new Dictionary<Metric, MetricStat>();
        foreach (var m in MetricInfo.All)
        {
            double avg = counts[m] > 0 ? sums[m] / counts[m] : 0;
            double peak = double.IsNaN(peaks[m]) ? 0 : peaks[m];
            stats[m] = new MetricStat(avg, peak, totals[m]);
        }
        return new SystemSummary(seconds, gapSeconds, stats);
    }
}
