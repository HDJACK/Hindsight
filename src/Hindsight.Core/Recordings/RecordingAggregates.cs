using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Recordings;

/// <summary>Everything one process accumulated over a range of ticks.</summary>
public sealed record ProcessTotals(int IdentityId, string Name, int Pid, double CpuSeconds, long ReadBytes, long WriteBytes,
    long NetBytes, long PeakWorkingSet, int Seconds, long FirstSeen, long LastSeen,
    float GpuPercentMax, long DiskReadBytes, long DiskWriteBytes, long PeakPrivateBytes);

/// <summary>Sums per-process activity over a range of ticks.</summary>
public static class RecordingAggregates
{
    public static IReadOnlyList<ProcessTotals> PerProcess(Recording r, long fromUnix, long toUnix) =>
        PerProcess(r.Ticks, r, r.Meta.CoreCount, fromUnix, toUnix);

    public static IReadOnlyList<ProcessTotals> PerProcess(IReadOnlyList<Tick> ticks, IIdentitySource ids, int coreCount, long fromUnix, long toUnix)
    {
        var acc = new Dictionary<int, (int pid, double cpu, long r, long w, long n, long peak, int sec, long first, long last,
            float gpuMax, long diskR, long diskW, long peakPrivate)>();
        foreach (var t in ticks)
        {
            if (t.UnixTime < fromUnix || t.UnixTime > toUnix || t.Has(TickFlags.Gap)) continue;
            int interval = Math.Max(1, (int)t.IntervalSeconds);
            // Invariant: one sample per identity per tick (Aggregator keys by pid), so summing samples in a tick never double-counts an identity.
            foreach (var s in t.Samples)
            {
                acc.TryGetValue(s.IdentityId, out var a);
                if (a.sec == 0) a.first = t.UnixTime;
                a.pid = s.Pid;
                a.cpu += s.CpuPercent / 100.0 * interval * Math.Max(1, coreCount);
                a.r += s.ReadBytes; a.w += s.WriteBytes; a.n += s.NetBytes;
                a.peak = Math.Max(a.peak, s.WorkingSet);
                a.sec += interval;
                a.last = t.UnixTime;
                a.gpuMax = Math.Max(a.gpuMax, s.GpuPercent);
                a.diskR += s.DiskReadBytes; a.diskW += s.DiskWriteBytes;
                a.peakPrivate = Math.Max(a.peakPrivate, s.PrivateBytes);
                acc[s.IdentityId] = a;
            }
        }
        return acc
            .Select(kv => new ProcessTotals(kv.Key, ids.Lookup(kv.Key)?.DisplayName ?? $"PID {kv.Value.pid}", kv.Value.pid, kv.Value.cpu,
                kv.Value.r, kv.Value.w, kv.Value.n, kv.Value.peak, kv.Value.sec, kv.Value.first, kv.Value.last,
                kv.Value.gpuMax, kv.Value.diskR, kv.Value.diskW, kv.Value.peakPrivate))
            .OrderByDescending(p => p.CpuSeconds).ThenByDescending(p => p.ReadBytes + p.WriteBytes)
            .ToList();
    }
}
