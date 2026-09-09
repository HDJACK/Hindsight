using System.Globalization;
using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>A system-wide signal that is sampled every tick and watched for anomalies.</summary>
public enum Metric { Cpu, Gpu, FileIo, DiskPhysical, Network, DiskLatency, Commit, HardFaults, RamFree, CpuMhz, ProcessCount, GpuMemory }

/// <summary>How <see cref="AnomalyDetector"/> turns a value and its baseline into "anomalous".</summary>
public enum RuleKind
{
    /// <summary>value >= median + k*MAD AND value >= 1.5 x median AND value >= noise floor.</summary>
    Ratio,
    /// <summary>value >= median + k*MAD AND value - median >= noise floor. For counters whose baseline is large, where a 1.5x rise never happens.</summary>
    Delta,
    /// <summary>Lower is worse: value &lt;= median - k*MAD AND value &lt;= InvertedRatio x median AND median - value >= noise floor.</summary>
    Inverted,
    /// <summary>Only the absolute threshold fires; the baseline path never does.</summary>
    ThresholdOnly,
}

/// <summary>Labels, detection rules and display formatting for each <see cref="Metric"/>.</summary>
public static class MetricInfo
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static readonly IReadOnlyList<Metric> All = Enum.GetValues<Metric>();

    public static string Label(Metric m) => m switch
    {
        Metric.Cpu => "CPU",
        Metric.Gpu => "GPU",
        Metric.FileIo => "File I/O",
        Metric.DiskPhysical => "Disk (phys)",
        Metric.Network => "Network",
        Metric.DiskLatency => "Disk latency",
        Metric.Commit => "Commit",
        Metric.HardFaults => "Hard faults",
        Metric.RamFree => "RAM free",
        Metric.CpuMhz => "CPU MHz",
        Metric.ProcessCount => "Processes",
        Metric.GpuMemory => "GPU memory",
        _ => m.ToString(),
    };

    public static bool LowerIsWorse(Metric m) => Rule(m) == RuleKind.Inverted;

    /// <summary>Which detection rule applies to a metric.</summary>
    public static RuleKind Rule(Metric m) => m switch
    {
        Metric.Commit => RuleKind.ThresholdOnly,
        Metric.RamFree or Metric.CpuMhz => RuleKind.Inverted,
        Metric.ProcessCount or Metric.GpuMemory => RuleKind.Delta,
        _ => RuleKind.Ratio,
    };

    /// <summary>For an <see cref="RuleKind.Inverted"/> metric, the fraction of the baseline a value must fall to; 1.0 (no
    /// fraction of baseline required) for every other metric, since 0 would make the ratio check trivially pass for any drop.</summary>
    public static double InvertedRatio(Metric m) => m switch
    {
        Metric.RamFree => 0.75,
        Metric.CpuMhz => 0.8,
        _ => 1.0,
    };

    public static LaneKind? Lane(Metric m) => m switch
    {
        Metric.Cpu => LaneKind.Cpu,
        Metric.Gpu => LaneKind.Gpu,
        Metric.FileIo => LaneKind.FileIo,
        Metric.DiskPhysical => LaneKind.DiskPhysical,
        Metric.Network => LaneKind.Network,
        Metric.DiskLatency => LaneKind.DiskLatency,
        Metric.Commit => LaneKind.Commit,
        Metric.RamFree => LaneKind.RamFree,
        Metric.HardFaults => LaneKind.RamFree,
        Metric.CpuMhz => LaneKind.CpuMhz,
        Metric.ProcessCount => LaneKind.Cpu,
        Metric.GpuMemory => LaneKind.Gpu,
        _ => null,
    };

    public static string Format(Metric m, double v) => m switch
    {
        Metric.Cpu or Metric.Gpu or Metric.Commit => v.ToString("0.0", En) + " %",
        Metric.FileIo or Metric.DiskPhysical or Metric.Network => Bytes(v) + "/s",
        Metric.DiskLatency => v.ToString("0.0", En) + " ms",
        Metric.HardFaults => v.ToString("0.#", En) + " /s",
        Metric.RamFree or Metric.GpuMemory => Bytes(v),
        Metric.CpuMhz => v.ToString("0", En) + " MHz",
        Metric.ProcessCount => v.ToString("0", En),
        _ => v.ToString(En),
    };

    public static string Bytes(double b)
    {
        if (b < 1024) return b.ToString("0.#", En) + " B";
        if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#", En) + " KB";
        if (b < 1024.0 * 1024 * 1024) return (b / 1048576.0).ToString("0.#", En) + " MB";
        return (b / 1073741824.0).ToString("0.##", En) + " GB";
    }
}
