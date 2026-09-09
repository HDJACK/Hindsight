using System.Globalization;
using System.Text;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Analysis;

/// <summary>Turns an Anomaly plus its surrounding ticks/markers into a human-readable Explanation.</summary>
public static class ExplanationBuilder
{
    /// <summary>Per-sample value used to attribute a metric to processes.</summary>
    public static double SampleValue(in ProcessSample s, Metric m) => m switch
    {
        Metric.Cpu => s.CpuPercent,
        Metric.Gpu => s.GpuPercent,
        Metric.FileIo => s.ReadBytes + s.WriteBytes,
        Metric.DiskPhysical => s.DiskReadBytes + s.DiskWriteBytes,
        Metric.Network => s.NetBytes,
        Metric.HardFaults => s.HardFaults,
        Metric.RamFree => s.WorkingSet,
        Metric.DiskLatency => s.DiskReadBytes + s.DiskWriteBytes,
        Metric.Commit => s.PrivateBytes,
        Metric.GpuMemory => s.GpuMemoryBytes,
        // Throttling has no per-process signal, so CPU time attributes it: who was busy while the clock was down.
        Metric.CpuMhz => s.CpuPercent,
        // A process storm has no per-sample count either; CPU time is the best available proxy for who drove it.
        Metric.ProcessCount => s.CpuPercent,
        _ => 0,
    };

    /// <summary>Top 3 contributors, foreground name, idle/OnAc state and in-range markers for an anomaly, plus the sentence describing it.</summary>
    public static Explanation Build(Anomaly a, IReadOnlyList<Tick> ticks, IIdentitySource ids, IReadOnlyList<Marker> markers, int windowTicks)
    {
        int firstIdx = -1;
        for (int i = 0; i < ticks.Count; i++)
        {
            if (a.Range.Contains(ticks[i].UnixTime)) { firstIdx = i; break; }
        }

        var rangeIdx = new List<int>();
        for (int i = 0; i < ticks.Count; i++)
        {
            if (a.Range.Contains(ticks[i].UnixTime) && !ticks[i].Has(TickFlags.Gap)) rangeIdx.Add(i);
        }
        int ticksInRange = rangeIdx.Count;

        var rangeSum = new Dictionary<int, double>();
        var pidById = new Dictionary<int, int>();
        foreach (int i in rangeIdx)
        {
            foreach (var s in ticks[i].Samples)
            {
                rangeSum.TryGetValue(s.IdentityId, out double cur);
                rangeSum[s.IdentityId] = cur + SampleValue(in s, a.Metric);
                pidById.TryAdd(s.IdentityId, s.Pid);
            }
        }
        double total = rangeSum.Values.Sum();

        var baseIdx = new List<int>();
        if (firstIdx >= 0)
        {
            int from = Math.Max(0, firstIdx - windowTicks);
            for (int i = from; i < firstIdx; i++)
            {
                if (!ticks[i].Has(TickFlags.Gap)) baseIdx.Add(i);
            }
        }
        var baseSum = new Dictionary<int, double>();
        foreach (int i in baseIdx)
        {
            foreach (var s in ticks[i].Samples)
            {
                baseSum.TryGetValue(s.IdentityId, out double cur);
                baseSum[s.IdentityId] = cur + SampleValue(in s, a.Metric);
            }
        }
        int baseCount = baseIdx.Count;

        var contributors = rangeSum
            .Select(kv =>
            {
                int identityId = kv.Key;
                double share = total > 0 ? kv.Value / total : 0;
                double value = ticksInRange > 0 ? kv.Value / ticksInRange : 0;
                double baselineValue = baseCount > 0 ? baseSum.GetValueOrDefault(identityId) / baseCount : 0;
                int pid = pidById[identityId];
                string name = ids.Lookup(identityId)?.DisplayName ?? $"PID {pid}";
                return new Contributor(identityId, name, pid, share, value, baselineValue);
            })
            // Identity id breaks ties so the same ticks always yield the same three contributors in the same order.
            .OrderByDescending(c => c.Share)
            .ThenBy(c => c.IdentityId)
            .Take(3)
            .ToList();

        string? foreground = null;
        var fgCounts = new Dictionary<int, int>();
        foreach (int i in rangeIdx)
        {
            int pid = ticks[i].ForegroundPid;
            if (pid == 0) continue;
            fgCounts.TryGetValue(pid, out int cur);
            fgCounts[pid] = cur + 1;
        }
        if (fgCounts.Count > 0)
        {
            // Lowest pid wins an equal count, so two foreground apps sharing the range do not flip between runs.
            int fgPid = fgCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            foreground = ids.ByPid(fgPid)?.DisplayName ?? $"PID {fgPid}";
        }

        bool idle = rangeIdx.Any(i => ticks[i].Has(TickFlags.UserIdle));

        bool hasHostState = ticks.Any(t => t.Has(TickFlags.OnAc) || t.BatteryPercent >= 0);
        bool? onAc = hasHostState && firstIdx >= 0 ? ticks[firstIdx].Has(TickFlags.OnAc) : null;

        var inRangeMarkers = markers.Where(m => a.Range.Contains(m.UnixTime)).OrderBy(m => m.UnixTime).ToList();

        string sentence = Sentence(a, contributors, foreground, idle, inRangeMarkers);

        return new Explanation(contributors, foreground, idle, onAc, inRangeMarkers, sentence);
    }

    public static string Sentence(Anomaly a, IReadOnlyList<Contributor> contributors, string? foreground, bool idle, IReadOnlyList<Marker> markers)
    {
        var sb = new StringBuilder();
        if (MetricInfo.Rule(a.Metric) == RuleKind.Inverted)
        {
            sb.Append($"{MetricInfo.Label(a.Metric)} down to {MetricInfo.Format(a.Metric, a.PeakValue)} for {a.Seconds} s (baseline {MetricInfo.Format(a.Metric, a.BaselineValue)})");
        }
        else if (MetricInfo.Rule(a.Metric) == RuleKind.Delta)
        {
            sb.Append($"{MetricInfo.Label(a.Metric)} up to {MetricInfo.Format(a.Metric, a.PeakValue)} for {a.Seconds} s (baseline {MetricInfo.Format(a.Metric, a.BaselineValue)})");
        }
        else
        {
            sb.Append($"{MetricInfo.Label(a.Metric)} {MetricInfo.Format(a.Metric, a.PeakValue)} for {a.Seconds} s (baseline {MetricInfo.Format(a.Metric, a.BaselineValue)})");
        }

        if (contributors.Count > 0)
        {
            string title = a.Metric == Metric.RamFree ? ": largest: " : ": ";
            sb.Append(title);
            sb.Append(string.Join(", ", contributors.Select(c => $"{c.Name} {Math.Round(c.Share * 100).ToString("0", CultureInfo.InvariantCulture)} %")));
        }

        if (foreground != null) sb.Append($"; you were in {foreground}");
        if (idle) sb.Append("; you were idle");

        foreach (var m in markers.Take(2))
        {
            string time = DateTimeOffset.FromUnixTimeSeconds(m.UnixTime).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            sb.Append($"; marker '{m.Text}' at {time}");
        }

        return sb.ToString();
    }
}
