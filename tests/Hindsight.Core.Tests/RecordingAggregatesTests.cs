using Hindsight.Core.Model;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

public class RecordingAggregatesTests
{
    private static Recording Make()
    {
        var ids = new[] { new ProcessIdentity(1, 1, "a.exe", "", "", 0), new ProcessIdentity(2, 1, "b.exe", "", "", 0) };
        var ticks = new List<Tick>
        {
            new() { UnixTime = 100, IntervalSeconds = 2, Samples = new[] { new ProcessSample { Pid = 1, IdentityId = 0, CpuPercent = 50, ReadBytes = 10, WorkingSet = 500, GpuPercent = 10, DiskReadBytes = 4, PrivateBytes = 100 }, new ProcessSample { Pid = 2, IdentityId = 1, CpuPercent = 10, NetBytes = 5 } } },
            new() { UnixTime = 102, IntervalSeconds = 2, Flags = TickFlags.Gap },
            new() { UnixTime = 104, IntervalSeconds = 2, Samples = new[] { new ProcessSample { Pid = 1, IdentityId = 0, CpuPercent = 25, WriteBytes = 20, WorkingSet = 700, GpuPercent = 30, DiskReadBytes = 6, PrivateBytes = 300 } } },
            new() { UnixTime = 106, IntervalSeconds = 2, Samples = new[] { new ProcessSample { Pid = 2, IdentityId = 1, CpuPercent = 90 } } },
        };
        var meta = new RecordingMeta(1, "t", "", 100, 106, 2, 4, "m", 4, "");
        return new Recording(meta, ticks, ids, Array.Empty<Marker>());
    }

    [Fact]
    public void PerProcess_SumsCpuSecondsWithIntervalAndCores()
    {
        var r = Make();
        var totals = RecordingAggregates.PerProcess(r, 100, 106);
        var a = totals.Single(t => t.Name == "a.exe");
        // (50/100*2*4) + (25/100*2*4) = 4 + 2
        Assert.Equal(6.0, a.CpuSeconds, 6);
        Assert.Equal(10, a.ReadBytes); Assert.Equal(20, a.WriteBytes); Assert.Equal(700, a.PeakWorkingSet);
        Assert.Equal(4, a.Seconds); Assert.Equal(100, a.FirstSeen); Assert.Equal(104, a.LastSeen);
        Assert.Equal(30, a.GpuPercentMax); Assert.Equal(10, a.DiskReadBytes); Assert.Equal(0, a.DiskWriteBytes); Assert.Equal(300, a.PeakPrivateBytes);
        var b = totals.Single(t => t.Name == "b.exe");
        Assert.Equal((10 + 90) / 100.0 * 2 * 4, b.CpuSeconds, 6);
        Assert.Equal(5, b.NetBytes);
        Assert.Equal("b.exe", totals[0].Name);   // 8 s > 6 s
    }

    [Fact]
    public void PerProcess_RangeFilter()
    {
        var r = Make();
        var totals = RecordingAggregates.PerProcess(r, 104, 106);
        Assert.Equal(2, totals.Count);
        Assert.Equal(2.0, totals.Single(t => t.Name == "a.exe").CpuSeconds, 6);
    }

    [Fact]
    public void PerProcess_UnknownIdentity_UsesPidName()
    {
        var ticks = new List<Tick> { new() { UnixTime = 1, IntervalSeconds = 1, Samples = new[] { new ProcessSample { Pid = 42, IdentityId = 7 } } } };
        var r = new Recording(new RecordingMeta(1, "t", "", 1, 1, 1, 1, "m", 1, ""), ticks, Array.Empty<ProcessIdentity>(), Array.Empty<Marker>());
        Assert.Equal("PID 42", RecordingAggregates.PerProcess(r, 0, 10)[0].Name);
    }
}
