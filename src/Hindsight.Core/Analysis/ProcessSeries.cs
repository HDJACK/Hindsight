using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Analysis;

/// <summary>One process's resource use in a single tick.</summary>
public readonly record struct ProcessPoint(float Cpu, float Gpu, long FileIo, long Disk, long Net, long WorkingSet, long PrivateBytes, int Handles, int Threads);

/// <summary>Per-process values aligned index-by-index with the ticks they came from. <c>SourceFirstUnix</c>/
/// <c>SourceLastUnix</c> are those ticks' first and last times: a consumer drawing the series over its own ticks must
/// compare them, because on a live source an equal <c>Points.Count</c> is no proof that the indexes still line up.</summary>
public sealed record ProcessSeries(int IdentityId, ProcessIdentity Identity, IReadOnlyList<ProcessPoint?> Points, long FirstSeen, long LastSeen, int PeakHandles, int PeakThreads, long PeakWorkingSet, string ParentChain, long SourceFirstUnix, long SourceLastUnix)
{
    /// <summary>Points[i] aligns with ticks[i]; null when the process has no sample in that tick.</summary>
    public static ProcessSeries? Extract(IReadOnlyList<Tick> ticks, IIdentitySource ids, int identityId)
    {
        var points = new ProcessPoint?[ticks.Count];
        bool seen = false;
        long firstSeen = 0, lastSeen = 0;
        int peakHandles = 0, peakThreads = 0;
        long peakWorkingSet = 0;
        int pid = 0;
        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            foreach (var s in t.Samples)
            {
                if (s.IdentityId != identityId) continue;
                points[i] = new ProcessPoint(s.CpuPercent, s.GpuPercent, s.ReadBytes + s.WriteBytes, s.DiskReadBytes + s.DiskWriteBytes, s.NetBytes, s.WorkingSet, s.PrivateBytes, s.HandleCount, s.ThreadCount);
                if (!seen) { seen = true; firstSeen = t.UnixTime; pid = s.Pid; }
                lastSeen = t.UnixTime;
                peakHandles = Math.Max(peakHandles, s.HandleCount);
                peakThreads = Math.Max(peakThreads, s.ThreadCount);
                peakWorkingSet = Math.Max(peakWorkingSet, s.WorkingSet);
                break;
            }
        }
        if (!seen) return null;

        var identity = ids.Lookup(identityId) ?? ProcessIdentity.Placeholder(pid, 0);
        string chain = Processes.ParentChain.Describe(identity, ids);
        return new ProcessSeries(identityId, identity, points, firstSeen, lastSeen, peakHandles, peakThreads, peakWorkingSet, chain, ticks[0].UnixTime, ticks[^1].UnixTime);
    }

    public double Value(int index, LaneKind lane)
    {
        var p = Points[index];
        if (p is null) return 0;
        var v = p.Value;
        return lane switch
        {
            LaneKind.Cpu => v.Cpu,
            LaneKind.Gpu => v.Gpu,
            LaneKind.FileIo => v.FileIo,
            LaneKind.DiskPhysical => v.Disk,
            LaneKind.Network => v.Net,
            LaneKind.RamFree => v.WorkingSet,
            _ => 0,
        };
    }

    /// <summary>Indexes at which the process has a sample, for stepping through its activity.</summary>
    public IReadOnlyList<int> IndexesWithSamples =>
        Enumerable.Range(0, Points.Count).Where(i => Points[i] is not null).ToList();
}
