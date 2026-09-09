using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Analysis;

/// <summary>One process's totals in both recordings of a comparison, with the deltas between them.</summary>
public sealed record CompareRow(string Name, double CpuA, double CpuB, float GpuA, float GpuB, long IoA, long IoB, long NetA, long NetB, long DiskA, long DiskB)
{
    public double CpuDelta => CpuB - CpuA;
    public long IoDelta => IoB - IoA;
    public long NetDelta => NetB - NetA;
    public long DiskDelta => DiskB - DiskA;
    public float GpuDelta => GpuB - GpuA;
}

/// <summary>Summaries, per-process rows and anomalies for both sides of a comparison.</summary>
public sealed record ComparisonResult(SystemSummary SummaryA, SystemSummary SummaryB, IReadOnlyList<CompareRow> Rows, IReadOnlyList<Anomaly> AnomaliesA, IReadOnlyList<Anomaly> AnomaliesB);

/// <summary>Compares two recordings side by side.</summary>
public static class RecordingComparer
{
    /// <summary>Whole-recording summaries, per-process rows joined by name (case-insensitive; identities with the same name are summed, GPU = max), sorted by max(CpuA, CpuB) desc, and explained anomalies for both.</summary>
    public static ComparisonResult Compare(Recording a, Recording b, AppSettings settings, CancellationToken ct = default)
    {
        var summaryA = RangeSummary.System(a.Ticks, new TimeRange(a.Meta.StartUnix, a.Meta.EndUnix));
        var summaryB = RangeSummary.System(b.Ticks, new TimeRange(b.Meta.StartUnix, b.Meta.EndUnix));
        var totalsA = RecordingAggregates.PerProcess(a, a.Meta.StartUnix, a.Meta.EndUnix);
        var totalsB = RecordingAggregates.PerProcess(b, b.Meta.StartUnix, b.Meta.EndUnix);
        var rows = Join(totalsA, totalsB);
        var anomaliesA = AnomalyDetector.DetectAndExplain(a.Ticks, a, a.Markers, settings, ct);
        var anomaliesB = AnomalyDetector.DetectAndExplain(b.Ticks, b, b.Markers, settings, ct);
        return new ComparisonResult(summaryA, summaryB, rows, anomaliesA, anomaliesB);
    }

    public static IReadOnlyList<CompareRow> Join(IReadOnlyList<ProcessTotals> a, IReadOnlyList<ProcessTotals> b)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in a) if (seen.Add(p.Name)) names.Add(p.Name);
        foreach (var p in b) if (seen.Add(p.Name)) names.Add(p.Name);

        var rows = names.Select(name =>
        {
            var inA = a.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var inB = b.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            double cpuA = inA.Sum(p => p.CpuSeconds), cpuB = inB.Sum(p => p.CpuSeconds);
            float gpuA = inA.Count > 0 ? inA.Max(p => p.GpuPercentMax) : 0;
            float gpuB = inB.Count > 0 ? inB.Max(p => p.GpuPercentMax) : 0;
            long ioA = inA.Sum(p => p.ReadBytes + p.WriteBytes), ioB = inB.Sum(p => p.ReadBytes + p.WriteBytes);
            long netA = inA.Sum(p => p.NetBytes), netB = inB.Sum(p => p.NetBytes);
            long diskA = inA.Sum(p => p.DiskReadBytes + p.DiskWriteBytes), diskB = inB.Sum(p => p.DiskReadBytes + p.DiskWriteBytes);
            return new CompareRow(name, cpuA, cpuB, gpuA, gpuB, ioA, ioB, netA, netB, diskA, diskB);
        });

        return rows.OrderByDescending(r => Math.Max(r.CpuA, r.CpuB)).ToList();
    }
}
