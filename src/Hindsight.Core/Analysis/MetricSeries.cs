using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>Reads one metric out of a list of ticks as a plain series of per-second values.</summary>
public static class MetricSeries
{
    /// <summary>Per-second value of a metric for one tick; double.NaN for Gap ticks.</summary>
    public static double Value(Tick t, Metric m)
    {
        if (t.Has(TickFlags.Gap)) return double.NaN;
        int interval = Math.Max(1, (int)t.IntervalSeconds);
        return m switch
        {
            Metric.Cpu => t.CpuTotalPercent,
            Metric.Gpu => t.GpuTotalPercent,
            Metric.FileIo => t.DiskBytes / (double)interval,
            Metric.DiskPhysical => SumDiskPhysical(t) / (double)interval,
            Metric.Network => t.NetBytes / (double)interval,
            Metric.DiskLatency => t.DiskLatencyMs,
            Metric.Commit => t.CommitPercent,
            Metric.HardFaults => t.HardFaultsPerSec,
            Metric.RamFree => t.RamAvailableBytes,
            // 0 means the counter had no reading (e.g. the PDH query failed for this tick); treat it as no data
            // rather than a real throttle-to-zero, so it never registers as hot and is excluded from the baseline.
            Metric.CpuMhz => t.CpuMhz == 0 ? double.NaN : t.CpuMhz,
            Metric.ProcessCount => t.ProcessCount,
            Metric.GpuMemory => SumGpuMemory(t),
            _ => 0,
        };
    }

    public static double[] Values(IReadOnlyList<Tick> ticks, Metric m)
    {
        var result = new double[ticks.Count];
        for (int i = 0; i < ticks.Count; i++) result[i] = Value(ticks[i], m);
        return result;
    }

    /// <summary>False when the recording predates the signal: every non-gap tick has 0 for Gpu/DiskPhysical/DiskLatency/Commit/HardFaults/CpuMhz/ProcessCount/GpuMemory. Cpu, FileIo, Network, RamFree are always true.</summary>
    public static bool IsRecorded(IReadOnlyList<Tick> ticks, Metric m)
    {
        if (m is Metric.Cpu or Metric.FileIo or Metric.Network or Metric.RamFree) return true;
        foreach (var t in ticks)
        {
            if (t.Has(TickFlags.Gap)) continue;
            double v = Value(t, m);
            // NaN means "no reading" (e.g. CpuMhz == 0), not a nonzero signal, so it doesn't count as recorded.
            if (!double.IsNaN(v) && v != 0) return true;
        }
        return false;
    }

    // A tick keeps only its top Tick.MaxSamples processes, so this sum misses whatever fell below the cut and can
    // step when that membership changes even though no single process actually jumped.
    private static long SumGpuMemory(Tick t)
    {
        long sum = 0;
        foreach (var s in t.Samples) sum += s.GpuMemoryBytes;
        return sum;
    }

    internal static long SumDiskPhysical(Tick t)
    {
        long sum = 0;
        foreach (var s in t.Samples) sum += s.DiskReadBytes + s.DiskWriteBytes;
        return sum;
    }
}
