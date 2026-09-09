using Hindsight.Core.Model;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Analysis;

/// <summary>score = wCpu*CPU% + wIo*(I/O share) + wNet*(Net share) + wMem*(RAM share).
/// Every term is a percentage (CPU % of all cores, share of this tick's file I/O, share of
/// network, share of physical RAM), so the weights compare like with like.</summary>
public sealed class WeightedProcessRanker : IProcessRanker
{
    private readonly Func<AppSettings> _settings;
    public WeightedProcessRanker(Func<AppSettings> settings) => _settings = settings;

    public double Score(in ProcessSample sample, in SystemTotals totals)
    {
        var s = _settings();
        double ioShare = totals.DiskBytes > 0 ? (sample.ReadBytes + sample.WriteBytes) * 100.0 / totals.DiskBytes : 0;
        double netShare = totals.NetBytes > 0 ? sample.NetBytes * 100.0 / totals.NetBytes : 0;
        double memShare = totals.RamTotalBytes > 0 ? sample.WorkingSet * 100.0 / totals.RamTotalBytes : 0;
        return s.WeightCpu * sample.CpuPercent + s.WeightIo * ioShare + s.WeightNet * netShare + s.WeightMemory * memShare + s.WeightGpu * sample.GpuPercent;
    }
}
