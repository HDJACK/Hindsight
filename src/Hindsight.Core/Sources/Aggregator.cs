using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Sources;

/// <summary>Merges one round of source snapshots into a single <see cref="Tick"/>.</summary>
public sealed class Aggregator
{
    private const int HistoryLength = 300;

    private readonly ProcessTable _table;
    private readonly IdentityStore _identities;
    private readonly IProcessRanker _ranker;
    private readonly ISpikeDetector _spikes;
    private readonly int _coreCount;
    private readonly List<Tick> _history = new();

    public Aggregator(ProcessTable table, IdentityStore identities, IProcessRanker ranker, ISpikeDetector spikes, int coreCount)
    {
        _table = table;
        _identities = identities;
        _ranker = ranker;
        _spikes = spikes;
        _coreCount = Math.Max(1, coreCount);
    }

    private sealed class Acc
    {
        public long StartTime;
        public double CpuSeconds;
        public long Read, Write, Net, WorkingSet;
        public SampleFlags Flags;
        public ProcessIdentity? Identity;
        public float Gpu;
        public long GpuMem, Private;
        public int Handles, Threads, HardFaults;
        public long DiskRead, DiskWrite;
    }

    public Tick Advance(long unixTime, double intervalSeconds, IReadOnlyList<SourceSnapshot> snapshots, bool etwActive)
    {
        if (intervalSeconds <= 0) intervalSeconds = 1;
        var knownBefore = new HashSet<(int, long)>(_table.All.Select(i => (i.Pid, i.StartTime)));
        var acc = new Dictionary<int, Acc>();
        var totals = new SystemTotals(0, 0, 0);
        int lost = 0;

        // Process starts.
        foreach (var snap in snapshots)
        {
            lost += snap.LostEvents;
            foreach (var id in snap.Started)
            {
                _table.Upsert(id);
            }
        }

        // Services. The map covers every live PID, so an identity missing from it has lost its services
        // (or never had any) and must be cleared. Updating the stored identity in place keeps its id:
        // Intern keys on (Pid, StartTime), which the service merge never touches.
        List<ProcessIdentity>? changedIdentities = null;
        foreach (var snap in snapshots)
        {
            var map = snap.Services;
            if (map is null) continue;
            foreach (var id in _table.All)
            {
                string svc = map.TryGetValue(id.Pid, out var s) ? s : "";
                var changed = _table.SetServices(id.Pid, svc);
                if (changed is not null) (changedIdentities ??= new List<ProcessIdentity>()).Add(changed);
            }
        }
        if (changedIdentities is not null) _identities.UpdateMany(changedIdentities);

        // Deltas. Only the snapshot source sets StartTime on its deltas; ETW deltas (e.g. disk I/O) leave
        // it 0. Tracking which PIDs the snapshot source actually saw keeps a short-lived process that only
        // produced ETW I/O from being mistaken for a long-lived one, which would suppress its booking below.
        var snapshotSeen = new HashSet<int>();
        foreach (var snap in snapshots)
        {
            totals = new SystemTotals(
                Math.Max(totals.CpuPercent, snap.Totals.CpuPercent),
                Math.Max(totals.RamAvailableBytes, snap.Totals.RamAvailableBytes),
                Math.Max(totals.ProcessCount, snap.Totals.ProcessCount),
                RamTotalBytes: Math.Max(totals.RamTotalBytes, snap.Totals.RamTotalBytes),
                GpuTotalPercent: Math.Max(totals.GpuTotalPercent, snap.Totals.GpuTotalPercent),
                CpuMhz: (ushort)Math.Max(totals.CpuMhz, snap.Totals.CpuMhz),
                DiskQueue: Math.Max(totals.DiskQueue, snap.Totals.DiskQueue),
                DiskLatencyMs: Math.Max(totals.DiskLatencyMs, snap.Totals.DiskLatencyMs),
                CommitPercent: Math.Max(totals.CommitPercent, snap.Totals.CommitPercent),
                HardFaultsPerSec: Math.Max(totals.HardFaultsPerSec, snap.Totals.HardFaultsPerSec),
                BatteryPercent: (sbyte)Math.Max(totals.BatteryPercent, snap.Totals.BatteryPercent),
                OnAc: totals.OnAc ?? snap.Totals.OnAc,
                ForegroundPid: totals.ForegroundPid != 0 ? totals.ForegroundPid : snap.Totals.ForegroundPid,
                UserIdle: totals.UserIdle || snap.Totals.UserIdle);

            foreach (var d in snap.Deltas)
            {
                if (!acc.TryGetValue(d.Pid, out var a)) acc[d.Pid] = a = new Acc();
                if (d.StartTime != 0)
                {
                    a.StartTime = d.StartTime;
                    snapshotSeen.Add(d.Pid);
                }
                a.CpuSeconds += d.CpuSeconds;
                a.Read += d.ReadBytes;
                a.Write += d.WriteBytes;
                a.Net += d.NetBytes;
                a.WorkingSet = Math.Max(a.WorkingSet, d.WorkingSet);
                a.Gpu = Math.Max(a.Gpu, d.GpuPercent);
                a.GpuMem = Math.Max(a.GpuMem, d.GpuMemoryBytes);
                a.Private = Math.Max(a.Private, d.PrivateBytes);
                a.Handles = Math.Max(a.Handles, d.HandleCount);
                a.Threads = Math.Max(a.Threads, d.ThreadCount);
                a.HardFaults += d.HardFaults;
                a.DiskRead += d.DiskReadBytes;
                a.DiskWrite += d.DiskWriteBytes;
            }
        }

        // Process exits.
        var toRemove = new List<ProcessIdentity>();
        foreach (var snap in snapshots)
        {
            foreach (var e in snap.Exited)
            {
                var id = e.Identity;
                bool seenInDeltas = snapshotSeen.Contains(id.Pid);
                bool shortLived = !seenInDeltas && !knownBefore.Contains((id.Pid, id.StartTime));
                // A snapshot exit for an unknown PID with nothing to show (no CPU/IO and no
                // ETW data already accumulated for it) would just be booked as short-lived
                // zeros; skip it since there's nothing worth displaying.
                bool nothingToShow = e.CpuSecondsEstimated == 0 && e.ReadBytes == 0 && e.WriteBytes == 0 && !seenInDeltas && !acc.ContainsKey(id.Pid);
                if (shortLived && !nothingToShow)
                {
                    if (acc.TryGetValue(id.Pid, out var existing))
                    {
                        // ETW already recorded I/O for this PID before the exit arrived;
                        // fold the exit totals into that entry instead of discarding it.
                        existing.CpuSeconds += e.CpuSecondsEstimated;
                        existing.Read += e.ReadBytes;
                        existing.Write += e.WriteBytes;
                        existing.Identity = id;
                        existing.Flags |= SampleFlags.ShortLived | SampleFlags.Estimated;
                    }
                    else
                    {
                        acc[id.Pid] = new Acc
                        {
                            StartTime = id.StartTime,
                            CpuSeconds = e.CpuSecondsEstimated,
                            Read = e.ReadBytes,
                            Write = e.WriteBytes,
                            Flags = SampleFlags.ShortLived | SampleFlags.Estimated,
                            Identity = id,
                        };
                    }
                }
                toRemove.Add(id);
            }
        }

        // Samples and totals.
        double cpuDivisor = intervalSeconds * _coreCount;
        var built = new List<ProcessSample>(acc.Count);
        long disk = 0, net = 0;
        foreach (var (pid, a) in acc)
        {
            var identity = a.Identity ?? (a.StartTime != 0 ? _table.Get(pid, a.StartTime) : null) ?? _table.GetByPid(pid);
            if (identity is null)
            {
                identity = ProcessIdentity.Placeholder(pid, a.StartTime);
                _table.Upsert(identity);
            }
            var sample = new ProcessSample
            {
                Pid = pid,
                IdentityId = _identities.Intern(identity),
                CpuPercent = (float)Math.Clamp(a.CpuSeconds / cpuDivisor * 100.0, 0, 100),
                ReadBytes = a.Read,
                WriteBytes = a.Write,
                NetBytes = a.Net,
                WorkingSet = a.WorkingSet,
                Flags = a.Flags,
                GpuPercent = a.Gpu,
                GpuMemoryBytes = a.GpuMem,
                PrivateBytes = a.Private,
                HandleCount = a.Handles,
                ThreadCount = a.Threads,
                HardFaults = a.HardFaults,
                DiskReadBytes = a.DiskRead,
                DiskWriteBytes = a.DiskWrite,
            };
            disk += a.Read + a.Write;
            net += a.Net;
            built.Add(sample);
        }
        totals = totals with { DiskBytes = disk, NetBytes = net };

        var samples = new List<(double score, ProcessSample sample)>(built.Count);
        foreach (var sample in built)
            samples.Add((_ranker.Score(in sample, in totals), sample));
        samples.Sort((x, y) => y.score.CompareTo(x.score));

        var tick = new Tick
        {
            UnixTime = unixTime,
            CpuTotalPercent = (float)totals.CpuPercent,
            DiskBytes = disk,
            NetBytes = net,
            RamAvailableBytes = totals.RamAvailableBytes,
            ProcessCount = totals.ProcessCount,
            LostEvents = lost,
            Flags = etwActive ? TickFlags.EtwActive : TickFlags.None,
            IntervalSeconds = (byte)Math.Clamp((int)Math.Round(intervalSeconds), 1, 255),
            Samples = samples.Take(Tick.MaxSamples).Select(x => x.sample).ToArray(),
            GpuTotalPercent = totals.GpuTotalPercent,
            CpuMhz = totals.CpuMhz,
            DiskQueue = totals.DiskQueue,
            DiskLatencyMs = totals.DiskLatencyMs,
            CommitPercent = totals.CommitPercent,
            HardFaultsPerSec = totals.HardFaultsPerSec,
            BatteryPercent = totals.BatteryPercent,
            ForegroundPid = totals.ForegroundPid,
        };
        if (totals.OnAc == true) tick.Flags |= TickFlags.OnAc;
        if (totals.UserIdle) tick.Flags |= TickFlags.UserIdle;

        var history = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_history);
        if (_spikes.IsSpike(tick, history)) tick.Flags |= TickFlags.Spike;
        _history.Add(tick);
        if (_history.Count > HistoryLength) _history.RemoveAt(0);

        foreach (var id in toRemove) _table.Remove(id.Pid, id.StartTime);
        return tick;
    }
}
