using Hindsight.Core.Model;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Analysis;

/// <summary>Absolute thresholds from settings. Byte counts are normalised to per-second rates via Tick.IntervalSeconds.</summary>
public sealed class ThresholdSpikeDetector : ISpikeDetector
{
    private readonly Func<AppSettings> _settings;
    public ThresholdSpikeDetector(Func<AppSettings> settings) => _settings = settings;

    public bool IsSpike(Tick current, ReadOnlySpan<Tick> history)
    {
        var s = _settings();
        double interval = Math.Max(1.0, current.IntervalSeconds);
        double ioMBps = current.DiskBytes / interval / 1_048_576.0;
        double netMBps = current.NetBytes / interval / 1_048_576.0;
        return current.CpuTotalPercent >= s.CpuSpikePercent
            || ioMBps >= s.FileIoSpikeMBps
            || netMBps >= s.NetworkSpikeMBps
            || current.GpuTotalPercent >= s.GpuSpikePercent
            || current.DiskLatencyMs >= s.DiskLatencySpikeMs
            || current.CommitPercent >= s.CommitSpikePercent;
    }
}
