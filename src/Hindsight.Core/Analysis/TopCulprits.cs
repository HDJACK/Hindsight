using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Analysis;

/// <summary>A process's totals over a range, its share of each system resource, and the resulting score.</summary>
public sealed record CulpritEntry(ProcessTotals Totals, double GpuSeconds, double ShareCpu, double ShareGpu, double ShareIo, double ShareDisk, double ShareNet, double ShareMemory, double Score);

/// <summary>Ranks the processes that account for most of the system's resource use over a range.</summary>
public static class TopCulprits
{
    /// <summary>Ranks processes over the range. Shares are 0–1 fractions of the system total over the same range
    /// (CPU: Σ CpuTotalPercent-seconds×cores; GPU: Σ GpuTotalPercent-seconds; I/O: Σ DiskBytes; disk: Σ sample disk bytes; net: Σ NetBytes;
    /// memory: peak working set / Σ peak working sets), each clamped to 1. Score = wCpu·ShareCpu + wGpu·ShareGpu + wIo·ShareIo + wIo·ShareDisk + wNet·ShareNet + wMem·ShareMemory. Sorted by Score desc, then CpuSeconds desc; at most n entries.</summary>
    public static IReadOnlyList<CulpritEntry> Rank(IReadOnlyList<Tick> ticks, IIdentitySource ids, int coreCount, TimeRange range, AppSettings weights, int n) =>
        Rank(ticks, RecordingAggregates.PerProcess(ticks, ids, coreCount, range.FromUnix, range.ToUnix), coreCount, range, weights, n);

    /// <summary>Same ranking over per-process totals the caller already computed for <paramref name="range"/>; use this
    /// when the same totals are needed elsewhere so <see cref="RecordingAggregates.PerProcess"/> runs once, not twice.</summary>
    public static IReadOnlyList<CulpritEntry> Rank(IReadOnlyList<Tick> ticks, IReadOnlyList<ProcessTotals> totals, int coreCount, TimeRange range, AppSettings weights, int n)
    {
        double sysCpuSeconds = 0, sysGpuSeconds = 0;
        long sysIoBytes = 0, sysDiskBytes = 0, sysNetBytes = 0;
        var gpuSecByIdentity = new Dictionary<int, double>();

        foreach (var t in ticks)
        {
            if (!range.Contains(t.UnixTime) || t.Has(TickFlags.Gap)) continue;
            int interval = Math.Max(1, (int)t.IntervalSeconds);

            sysCpuSeconds += t.CpuTotalPercent / 100.0 * interval * Math.Max(1, coreCount);
            sysGpuSeconds += t.GpuTotalPercent / 100.0 * interval;
            sysIoBytes += t.DiskBytes;
            sysNetBytes += t.NetBytes;

            foreach (var s in t.Samples)
            {
                sysDiskBytes += s.DiskReadBytes + s.DiskWriteBytes;
                gpuSecByIdentity.TryGetValue(s.IdentityId, out var gpuSec);
                gpuSecByIdentity[s.IdentityId] = gpuSec + s.GpuPercent / 100.0 * interval;
            }
        }

        long sysPeakWorkingSet = totals.Sum(p => p.PeakWorkingSet);

        var entries = totals.Select(p =>
        {
            double gpuSeconds = gpuSecByIdentity.GetValueOrDefault(p.IdentityId);
            double shareCpu = Share(p.CpuSeconds, sysCpuSeconds);
            double shareGpu = Share(gpuSeconds, sysGpuSeconds);
            double shareIo = Share(p.ReadBytes + p.WriteBytes, sysIoBytes);
            double shareDisk = Share(p.DiskReadBytes + p.DiskWriteBytes, sysDiskBytes);
            double shareNet = Share(p.NetBytes, sysNetBytes);
            double shareMemory = Share(p.PeakWorkingSet, sysPeakWorkingSet);
            double score = weights.WeightCpu * shareCpu + weights.WeightGpu * shareGpu + weights.WeightIo * shareIo
                + weights.WeightIo * shareDisk + weights.WeightNet * shareNet + weights.WeightMemory * shareMemory;
            return new CulpritEntry(p, gpuSeconds, shareCpu, shareGpu, shareIo, shareDisk, shareNet, shareMemory, score);
        });

        return entries.OrderByDescending(e => e.Score).ThenByDescending(e => e.Totals.CpuSeconds).Take(n).ToList();
    }

    private static double Share(double x, double total) => total > 0 ? Math.Min(1, x / total) : 0;
}
