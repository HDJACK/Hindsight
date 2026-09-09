using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Analysis;

/// <summary>Detects per-metric anomalies from absolute thresholds and a rolling median/MAD baseline.</summary>
public static class AnomalyDetector
{
    /// <summary>Detects anomalies for every recorded metric, sorted by Severity desc then Range.FromUnix asc.
    /// Explanation is left empty; <see cref="DetectAndExplain"/> fills it in.</summary>
    public static IReadOnlyList<Anomaly> Detect(IReadOnlyList<Tick> ticks, AppSettings s, CancellationToken ct = default)
    {
        var result = new List<Anomaly>();
        if (ticks.Count == 0) return result;
        int interval = Math.Max(1, (int)ticks[0].IntervalSeconds);
        int window = Math.Max(10, s.BaselineWindowSeconds / interval);
        double k = s.SensitivityK;
        foreach (var m in MetricInfo.All)
        {
            ct.ThrowIfCancellationRequested();
            if (!MetricSeries.IsRecorded(ticks, m)) continue;
            var values = MetricSeries.Values(ticks, m);
            var (med, mad) = Baseline.Rolling(values, window);
            double? threshold = AbsoluteThreshold(m, s);
            double floor = NoiseFloor(m);
            var rule = MetricInfo.Rule(m);
            bool lower = rule == RuleKind.Inverted;
            double invRatio = MetricInfo.InvertedRatio(m);
            int runStart = -1, runEnd = -1, bridge = 0;
            double peak = 0, peakBase = 0; bool hitThreshold = false;
            for (int i = 0; i <= ticks.Count; i++)
            {
                bool gap = i == ticks.Count || ticks[i].Has(TickFlags.Gap);
                bool hot = false;
                if (!gap)
                {
                    double v = values[i];
                    if (threshold is double th && !lower && v >= th) hot = true;
                    else hot = rule switch
                    {
                        RuleKind.Inverted => v <= med[i] - k * mad[i] && v <= invRatio * med[i] && med[i] - v >= floor,
                        RuleKind.Delta => v >= med[i] + k * mad[i] && v - med[i] >= floor,
                        RuleKind.ThresholdOnly => false,
                        _ => v >= med[i] + k * mad[i] && v >= 1.5 * med[i] && v >= floor,
                    };
                }
                if (hot)
                {
                    double v = values[i];
                    if (runStart < 0) { runStart = i; peak = v; peakBase = med[i]; hitThreshold = false; }
                    else if (lower ? v < peak : v > peak) { peak = v; peakBase = med[i]; }
                    if (threshold is double t2 && !lower && v >= t2) hitThreshold = true;
                    runEnd = i; bridge = 0;
                }
                else if (runStart >= 0 && (gap || ++bridge > 2))
                {
                    Flush(); runStart = -1; bridge = 0;
                }
            }

            void Flush()
            {
                var range = new TimeRange(ticks[runStart].UnixTime, ticks[runEnd].UnixTime);
                int seconds = 0;
                for (int j = runStart; j <= runEnd; j++) seconds += Math.Max(1, (int)ticks[j].IntervalSeconds);
                if (seconds < s.MinAnomalySeconds && !hitThreshold) return;
                result.Add(new Anomaly(m, range, peak, peakBase, Classify(m, peak, peakBase, threshold), seconds, Explanation.Empty));
            }
        }
        return result.OrderByDescending(a => a.Severity).ThenBy(a => a.Range.FromUnix).ToList();
    }

    /// <summary>Detect then fill each anomaly's Explanation via ExplanationBuilder.Build.</summary>
    public static IReadOnlyList<Anomaly> DetectAndExplain(IReadOnlyList<Tick> ticks, IIdentitySource ids, IReadOnlyList<Marker> markers, AppSettings s, CancellationToken ct = default)
    {
        var anomalies = Detect(ticks, s, ct);
        if (anomalies.Count == 0) return anomalies;
        int interval = Math.Max(1, (int)ticks[0].IntervalSeconds);
        int windowTicks = Math.Max(10, s.BaselineWindowSeconds / interval);
        var explained = new List<Anomaly>(anomalies.Count);
        foreach (var a in anomalies)
        {
            // One pass over the ticks per anomaly, so a long list stays as cancellable as detection itself.
            ct.ThrowIfCancellationRequested();
            explained.Add(a with { Explanation = ExplanationBuilder.Build(a, ticks, ids, markers, windowTicks) });
        }
        return explained;
    }

    /// <summary>The configured absolute threshold for a metric, in the metric's own unit; null when it has none.</summary>
    public static double? AbsoluteThreshold(Metric m, AppSettings s) => m switch
    {
        Metric.Cpu => s.CpuSpikePercent,
        Metric.Gpu => s.GpuSpikePercent,
        Metric.FileIo => s.FileIoSpikeMBps * 1_048_576,
        Metric.Network => s.NetworkSpikeMBps * 1_048_576,
        Metric.DiskLatency => s.DiskLatencySpikeMs,
        Metric.Commit => s.CommitSpikePercent,
        _ => null,
    };

    /// <summary>Smallest change worth flagging, below which a metric is treated as noise even when it clears median + k·MAD.
    /// For an inverted metric this is the minimum drop; Commit returns +∞ so only its absolute threshold can fire.</summary>
    public static double NoiseFloor(Metric m) => m switch
    {
        Metric.Cpu => 20,
        Metric.Gpu => 20,
        Metric.FileIo => 2 * 1_048_576,
        Metric.DiskPhysical => 2 * 1_048_576,
        Metric.Network => 256 * 1024,
        Metric.DiskLatency => 5,
        Metric.HardFaults => 100,
        Metric.RamFree => 256 * 1_048_576,
        Metric.CpuMhz => 300,
        Metric.ProcessCount => 20,
        Metric.GpuMemory => 512 * 1_048_576,
        Metric.Commit => double.PositiveInfinity,
        _ => 0,
    };

    /// <summary>Severity from the peak/baseline ratio (for a Delta metric, from the rise measured in noise floors):
    /// &lt; 2 Low, &lt; 4 Medium, else High, and a peak at or above the absolute threshold is lifted to at least Medium.
    /// A zero baseline says nothing about how far the peak rose, so it scores as no rise at all; for a lower-is-worse
    /// metric a peak of 0 against a real baseline is the worst case the ratio can express and stays High.</summary>
    public static Severity Classify(Metric m, double peak, double baseline, double? threshold)
    {
        var rule = MetricInfo.Rule(m);
        bool lower = rule == RuleKind.Inverted;
        // A Delta metric has no meaningful peak/baseline ratio, so it scores the rise in units of its own noise
        // floor. Math.Max guards the division: a zero floor would turn the rise into NaN or +infinity.
        double ratio = rule == RuleKind.Delta
            ? (peak - baseline) / Math.Max(1, NoiseFloor(m))
            : lower ? (peak > 0 ? baseline / peak : baseline > 0 ? 4 : 1) : (baseline > 0 ? peak / baseline : 1);
        Severity sev = ratio < 2 ? Severity.Low : ratio < 4 ? Severity.Medium : Severity.High;
        if (threshold is double t && !lower && peak >= t && sev == Severity.Low) sev = Severity.Medium;
        return sev;
    }
}
