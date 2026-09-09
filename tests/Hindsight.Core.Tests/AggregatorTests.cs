using System.Linq;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;
using Hindsight.Core.Sources;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class AggregatorTests
{
    private sealed class StubSpike(bool result) : ISpikeDetector
    {
        public int Calls;
        public int LastHistoryLength;
        public bool IsSpike(Tick c, ReadOnlySpan<Tick> h) { Calls++; LastHistoryLength = h.Length; return result; }
    }

    private static (Aggregator agg, ProcessTable table, IdentityStore ids) Make(ISpikeDetector? spike = null, int cores = 2)
    {
        var table = new ProcessTable();
        var ids = new IdentityStore(null);
        return (new Aggregator(table, ids, new WeightedProcessRanker(() => AppSettings.Default), spike ?? new StubSpike(false), cores), table, ids);
    }

    private static ProcessIdentity Id(int pid, long start = 100, string name = "p.exe") => new(pid, start, name, "", "", 1);

    [Fact]
    public void Merges_SnapshotCpu_With_EtwDisk_ForSamePid()
    {
        var (agg, _, ids) = Make();
        var snap = new SourceSnapshot { Totals = new SystemTotals(30, 1000, 5) };
        snap.Started.Add(Id(10));
        snap.Deltas.Add(new ProcessDelta(10, 100, CpuSeconds: 1.0, 0, 0, 0, WorkingSet: 500));
        var etw = new SourceSnapshot();
        etw.Deltas.Add(new ProcessDelta(10, 0, 0, ReadBytes: 4096, WriteBytes: 1024, NetBytes: 77, 0));

        var tick = agg.Advance(1000, 1.0, new[] { snap, etw }, etwActive: true);

        var s = Assert.Single(tick.Samples);
        Assert.Equal(10, s.Pid);
        Assert.Equal(50f, s.CpuPercent);          // 1 s CPU on 2 cores
        Assert.Equal(4096, s.ReadBytes);
        Assert.Equal(1024, s.WriteBytes);
        Assert.Equal(77, s.NetBytes);
        Assert.Equal(500, s.WorkingSet);
        Assert.Equal("p.exe", ids.Lookup(s.IdentityId)!.Name);
        Assert.Equal(5120, tick.DiskBytes);
        Assert.Equal(77, tick.NetBytes);
        Assert.Equal(30f, tick.CpuTotalPercent);
        Assert.Equal(1000, tick.RamAvailableBytes);
        Assert.Equal(5, tick.ProcessCount);
        Assert.True(tick.Has(TickFlags.EtwActive));
        Assert.Equal(1, tick.IntervalSeconds);
    }

    [Fact]
    public void Advance_StoresRoundedIntervalOnTick()
    {
        var (agg, _, _) = Make();
        Assert.Equal(3, agg.Advance(1, 3.4, new[] { new SourceSnapshot() }, false).IntervalSeconds);
        Assert.Equal(1, agg.Advance(2, 0.2, new[] { new SourceSnapshot() }, false).IntervalSeconds);
    }

    [Fact]
    public void CpuPercent_IsClampedTo100()
    {
        var (agg, _, _) = Make(cores: 1);
        var snap = new SourceSnapshot();
        snap.Deltas.Add(new ProcessDelta(1, 5, CpuSeconds: 3.0, 0, 0, 0, 0));
        var tick = agg.Advance(1, 1.0, new[] { snap }, false);
        Assert.Equal(100f, tick.Samples[0].CpuPercent);
    }

    [Fact]
    public void ShortLivedExit_IsBookedWithFlags()
    {
        var (agg, table, _) = Make();
        var etw = new SourceSnapshot();
        etw.Exited.Add(new ProcessExit(Id(55, name: "flash.exe"), CpuSecondsEstimated: 0.5, ReadBytes: 10, WriteBytes: 20));
        var tick = agg.Advance(1, 1.0, new[] { etw }, true);
        var s = Assert.Single(tick.Samples);
        Assert.Equal(55, s.Pid);
        Assert.Equal(25f, s.CpuPercent);
        Assert.Equal(10, s.ReadBytes);
        Assert.Equal(SampleFlags.ShortLived | SampleFlags.Estimated, s.Flags);
        Assert.Null(table.GetByPid(55));
    }

    [Fact]
    public void StartedAndExitedInSameTick_IsBooked()
    {
        var (agg, _, _) = Make();
        var etw = new SourceSnapshot();
        etw.Started.Add(Id(56));
        etw.Exited.Add(new ProcessExit(Id(56), 0.2, 0, 0));
        var tick = agg.Advance(1, 1.0, new[] { etw }, true);
        Assert.Single(tick.Samples);
        Assert.True((tick.Samples[0].Flags & SampleFlags.ShortLived) != 0);
    }

    [Fact]
    public void LongLivedExit_IsNotBooked_AndRemovedFromTable()
    {
        var (agg, table, _) = Make();
        var first = new SourceSnapshot();
        first.Started.Add(Id(60));
        first.Deltas.Add(new ProcessDelta(60, 100, 0.1, 0, 0, 0, 0));
        agg.Advance(1, 1.0, new[] { first }, true);
        Assert.NotNull(table.GetByPid(60));

        var second = new SourceSnapshot();
        second.Exited.Add(new ProcessExit(Id(60), 999, 999, 999));
        var tick = agg.Advance(2, 1.0, new[] { second }, true);
        Assert.Empty(tick.Samples);
        Assert.Null(table.GetByPid(60));
    }

    [Fact]
    public void ExitSeenInDeltas_UsesDeltasNotTotals()
    {
        var (agg, _, _) = Make();
        var snap = new SourceSnapshot();
        snap.Deltas.Add(new ProcessDelta(61, 100, 0.2, 0, 0, 0, 0));
        snap.Exited.Add(new ProcessExit(Id(61), 999, 999, 999));
        var tick = agg.Advance(1, 1.0, new[] { snap }, true);
        var s = Assert.Single(tick.Samples);
        Assert.Equal(10f, s.CpuPercent);
        Assert.Equal(0, s.ReadBytes);
    }

    [Fact]
    public void UnknownPid_GetsPlaceholderIdentity()
    {
        var (agg, _, ids) = Make();
        var etw = new SourceSnapshot();
        etw.Deltas.Add(new ProcessDelta(777, 0, 0, 100, 0, 0, 0));
        var tick = agg.Advance(1, 1.0, new[] { etw }, true);
        Assert.Equal("PID 777", ids.Lookup(tick.Samples[0].IdentityId)!.Name);
    }

    [Fact]
    public void KeepsTop40_ButSumsAllForTotals()
    {
        var (agg, _, _) = Make();
        var snap = new SourceSnapshot();
        for (int i = 1; i <= 50; i++)
            snap.Deltas.Add(new ProcessDelta(i, i, 0, ReadBytes: i, 0, 0, 0));
        var tick = agg.Advance(1, 1.0, new[] { snap }, false);
        Assert.Equal(Tick.MaxSamples, tick.Samples.Length);
        Assert.Equal(50, tick.Samples[0].Pid);          // highest score first
        Assert.Equal(11, tick.Samples[^1].Pid);
        Assert.Equal(50 * 51 / 2, tick.DiskBytes);
    }

    [Fact]
    public void Advance_ScoresWithTickTotals_SoCpuCulpritBeatsIoNoise()
    {
        var (agg, _, _) = Make(cores: 1);
        var snap = new SourceSnapshot { Totals = new SystemTotals(60, 1, 3, RamTotalBytes: 1L << 30) };
        snap.Deltas.Add(new ProcessDelta(1, 1, CpuSeconds: 0.5, 0, 0, 0, 0));           // 50 % CPU
        snap.Deltas.Add(new ProcessDelta(2, 2, 0, ReadBytes: 60L << 20, 0, 0, 0));     // 60 % of I/O
        snap.Deltas.Add(new ProcessDelta(3, 3, 0, ReadBytes: 40L << 20, 0, 0, 0));     // 40 % of I/O
        var tick = agg.Advance(1, 1.0, new[] { snap }, false);
        Assert.Equal(2, tick.Samples[0].Pid);   // 60 > 50
        Assert.Equal(1, tick.Samples[1].Pid);   // 50 > 40
        Assert.Equal(3, tick.Samples[2].Pid);
    }

    [Fact]
    public void SpikeFlag_FollowsDetector_AndHistoryGrows()
    {
        var spike = new StubSpike(true);
        var (agg, _, _) = Make(spike);
        agg.Advance(1, 1.0, new[] { new SourceSnapshot() }, false);
        var t2 = agg.Advance(2, 1.0, new[] { new SourceSnapshot() }, false);
        Assert.True(t2.Has(TickFlags.Spike));
        Assert.Equal(2, spike.Calls);
        Assert.Equal(1, spike.LastHistoryLength);
    }

    [Fact]
    public void LostEvents_AreSummed()
    {
        var (agg, _, _) = Make();
        var a = new SourceSnapshot { LostEvents = 2 };
        var b = new SourceSnapshot { LostEvents = 3 };
        Assert.Equal(5, agg.Advance(1, 1.0, new[] { a, b }, true).LostEvents);
    }

    [Fact]
    public void PidReuseWithinTick_LongLivedExit_KeepsNewProcessInTable()
    {
        var (agg, table, _) = Make();
        var first = new SourceSnapshot();
        first.Started.Add(Id(5, 100, "old.exe"));
        first.Deltas.Add(new ProcessDelta(5, 100, 0.1, 0, 0, 0, 0));
        agg.Advance(1, 1.0, new[] { first }, true);

        var second = new SourceSnapshot();
        second.Started.Add(Id(5, 200, "new.exe"));
        second.Exited.Add(new ProcessExit(Id(5, 100, "old.exe"), 999, 999, 999));
        var tick = agg.Advance(2, 1.0, new[] { second }, true);

        Assert.Empty(tick.Samples);
        Assert.Equal("new.exe", table.GetByPid(5)!.Name);
        Assert.Equal(200, table.GetByPid(5)!.StartTime);
    }

    [Fact]
    public void ShortLivedExit_WithEtwIoDelta_IsStillBookedAndMerged()
    {
        var (agg, _, ids) = Make();
        var etw = new SourceSnapshot();
        etw.Deltas.Add(new ProcessDelta(88, 0, 0, ReadBytes: 4096, 0, 0, 0));
        etw.Exited.Add(new ProcessExit(Id(88, name: "flash.exe"), CpuSecondsEstimated: 0.5, ReadBytes: 10, WriteBytes: 20));
        var tick = agg.Advance(1, 1.0, new[] { etw }, true);
        var s = Assert.Single(tick.Samples);
        Assert.Equal(25f, s.CpuPercent);
        Assert.Equal(4106, s.ReadBytes);
        Assert.Equal(20, s.WriteBytes);
        Assert.Equal(SampleFlags.ShortLived | SampleFlags.Estimated, s.Flags);
        Assert.Equal("flash.exe", ids.Lookup(s.IdentityId)!.Name);
    }

    [Fact]
    public void Advance_MergesNewPerProcessFields()
    {
        var (agg, _, _) = Make();
        var snap1 = new SourceSnapshot();
        snap1.Deltas.Add(new ProcessDelta(10, 100, 0, 0, 0, 0, 0,
            GpuPercent: 10, GpuMemoryBytes: 5, PrivateBytes: 1, HandleCount: 3, ThreadCount: 1, HardFaults: 1,
            DiskReadBytes: 10, DiskWriteBytes: 1));
        var snap2 = new SourceSnapshot();
        snap2.Deltas.Add(new ProcessDelta(10, 100, 0, 0, 0, 0, 0,
            GpuPercent: 30, GpuMemoryBytes: 7, PrivateBytes: 2, HandleCount: 4, ThreadCount: 2, HardFaults: 2,
            DiskReadBytes: 5, DiskWriteBytes: 1));

        var tick = agg.Advance(1, 1.0, new[] { snap1, snap2 }, false);

        var s = Assert.Single(tick.Samples);
        Assert.Equal(30f, s.GpuPercent);
        Assert.Equal(7, s.GpuMemoryBytes);
        Assert.Equal(2, s.PrivateBytes);
        Assert.Equal(4, s.HandleCount);
        Assert.Equal(2, s.ThreadCount);
        Assert.Equal(3, s.HardFaults);
        Assert.Equal(15, s.DiskReadBytes);
        Assert.Equal(2, s.DiskWriteBytes);
    }

    [Fact]
    public void Advance_MergesNewTotals()
    {
        var (agg, _, _) = Make();
        var snap1 = new SourceSnapshot
        {
            Totals = new SystemTotals(0, 0, 0, GpuTotalPercent: 20, CpuMhz: 0, DiskLatencyMs: 2, CommitPercent: 50,
                HardFaultsPerSec: 1, BatteryPercent: -1, OnAc: null, ForegroundPid: 0, UserIdle: false),
        };
        var snap2 = new SourceSnapshot
        {
            Totals = new SystemTotals(0, 0, 0, GpuTotalPercent: 40, CpuMhz: 3800, DiskLatencyMs: 9, CommitPercent: 70,
                HardFaultsPerSec: 5, BatteryPercent: 77, OnAc: true, ForegroundPid: 1234, UserIdle: true),
        };

        var tick = agg.Advance(1, 1.0, new[] { snap1, snap2 }, false);

        Assert.Equal(40f, tick.GpuTotalPercent);
        Assert.Equal((ushort)3800, tick.CpuMhz);
        Assert.Equal(9f, tick.DiskLatencyMs);
        Assert.Equal(70f, tick.CommitPercent);
        Assert.Equal(5u, tick.HardFaultsPerSec);
        Assert.Equal((sbyte)77, tick.BatteryPercent);
        Assert.True(tick.Has(TickFlags.OnAc));
        Assert.Equal(1234, tick.ForegroundPid);
        Assert.True(tick.Has(TickFlags.UserIdle));
    }

    [Fact]
    public void PidReuseWithinTick_ShortLivedExit_IsBookedAndNewProcessKept()
    {
        var (agg, table, ids) = Make();
        var snap = new SourceSnapshot();
        snap.Started.Add(Id(7, 200, "new.exe"));
        snap.Exited.Add(new ProcessExit(Id(7, 100, "flash.exe"), 0.5, 10, 20));
        var tick = agg.Advance(1, 1.0, new[] { snap }, true);

        var s = Assert.Single(tick.Samples);
        Assert.Equal("flash.exe", ids.Lookup(s.IdentityId)!.Name);
        Assert.Equal(SampleFlags.ShortLived | SampleFlags.Estimated, s.Flags);
        Assert.Equal("new.exe", table.GetByPid(7)!.Name);
    }

    [Fact]
    public void Advance_GpuOnlyDelta_MergesIntoSnapshotSample()
    {
        var (agg, _, ids) = Make();
        var snap = new SourceSnapshot();
        snap.Started.Add(Id(5, 100, "game.exe"));
        snap.Deltas.Add(new ProcessDelta(5, 100, CpuSeconds: 0.5, 0, 0, 0, 0));
        var pdh = new SourceSnapshot();
        pdh.Deltas.Add(new ProcessDelta(5, 0, 0, 0, 0, 0, 0, GpuPercent: 42));

        var tick = agg.Advance(1, 1.0, new[] { snap, pdh }, false);

        var s = Assert.Single(tick.Samples);
        Assert.Equal(5, s.Pid);
        Assert.Equal(42f, s.GpuPercent);
        Assert.True(s.CpuPercent > 0);
        Assert.NotEqual("PID 5", ids.Lookup(s.IdentityId)!.Name);
    }

    [Fact]
    public void Advance_AllSourcesWithoutBattery_KeepsUnknown()
    {
        var (agg, _, _) = Make();
        var a = new SourceSnapshot { Totals = new SystemTotals(10, 1, 1) };
        var b = new SourceSnapshot { Totals = new SystemTotals(20, 1, 1) };
        var tick = agg.Advance(1, 1.0, new[] { a, b }, false);
        Assert.Equal(-1, tick.BatteryPercent);
        Assert.False(tick.Has(TickFlags.OnAc));
    }

    [Fact]
    public void Advance_MergesServicesIntoIdentities()
    {
        var (agg, table, ids) = Make();
        table.Upsert(Id(900, name: "svchost.exe"));
        table.Upsert(Id(901, name: "svchost2.exe"));
        var snap = new SourceSnapshot
        {
            Totals = new SystemTotals(10, 1, 1),
            Services = new Dictionary<int, string> { [900] = "wuauserv", [901] = "BITS" },
        };
        snap.Deltas.Add(new ProcessDelta(900, 100, CpuSeconds: 0.1, 0, 0, 0, WorkingSet: 10));
        snap.Deltas.Add(new ProcessDelta(901, 100, CpuSeconds: 0.1, 0, 0, 0, WorkingSet: 10));

        var tick = agg.Advance(1000, 1.0, new[] { snap }, false);
        Assert.Equal(2, tick.Samples.Length);
        var s = tick.Samples.Single(x => x.Pid == 900);
        var s901 = tick.Samples.Single(x => x.Pid == 901);
        Assert.Equal("wuauserv", ids.Lookup(s.IdentityId)!.Services);
        Assert.Equal("svchost.exe (wuauserv)", ids.Lookup(s.IdentityId)!.DisplayName);
        Assert.Equal("BITS", ids.Lookup(s901.IdentityId)!.Services);
        Assert.Equal("svchost2.exe (BITS)", ids.Lookup(s901.IdentityId)!.DisplayName);

        var snap2 = new SourceSnapshot { Totals = new SystemTotals(10, 1, 1), Services = new Dictionary<int, string>() };
        snap2.Deltas.Add(new ProcessDelta(900, 100, CpuSeconds: 0.1, 0, 0, 0, WorkingSet: 10));
        snap2.Deltas.Add(new ProcessDelta(901, 100, CpuSeconds: 0.1, 0, 0, 0, WorkingSet: 10));
        var tick2 = agg.Advance(1001, 1.0, new[] { snap2 }, false);
        Assert.Equal(2, tick2.Samples.Length);
        var s2 = tick2.Samples.Single(x => x.Pid == 900);
        var s2_901 = tick2.Samples.Single(x => x.Pid == 901);
        Assert.Equal(s.IdentityId, s2.IdentityId);
        Assert.Equal(s901.IdentityId, s2_901.IdentityId);
        Assert.Equal("", ids.Lookup(s2.IdentityId)!.Services);
        Assert.Equal("", ids.Lookup(s2_901.IdentityId)!.Services);
    }
}
