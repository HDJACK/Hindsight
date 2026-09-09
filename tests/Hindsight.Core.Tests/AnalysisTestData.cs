using Hindsight.Core.Model;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

/// <summary>Synthetic ticks for the analysis tests. Identities: 0 = a.exe (pid 1), 1 = b.exe (pid 2), 2 = c.exe (pid 3, parent 1).</summary>
public static class AnalysisTestData
{
    public static readonly ProcessIdentity[] Ids =
    {
        new(1, 1, "a.exe", @"C:\a.exe", "a.exe --x", 0),
        new(2, 1, "b.exe", @"C:\b.exe", "", 0),
        new(3, 1, "c.exe", @"C:\c.exe", "", 1),
    };

    public static Recording AsRecording(IReadOnlyList<Tick> ticks, IReadOnlyList<Marker>? markers = null) =>
        new(new RecordingMeta(2, "t", "", ticks[0].UnixTime, ticks[^1].UnixTime, ticks[0].IntervalSeconds, 4, "m", ticks.Count, ""),
            ticks, Ids, markers ?? Array.Empty<Marker>());

    public static Tick Tick(long unix, float cpu = 10, params ProcessSample[] samples) =>
        TickAt(unix, 1, cpu, samples);

    /// <summary>Like <see cref="Tick"/> but with an explicit sample interval, for the interval &gt; 1 s fixtures.</summary>
    public static Tick TickAt(long unix, int intervalSeconds, float cpu = 10, params ProcessSample[] samples) =>
        new() { UnixTime = unix, CpuTotalPercent = cpu, IntervalSeconds = (byte)intervalSeconds, RamAvailableBytes = 8L << 30, Flags = TickFlags.EtwActive, Samples = samples };

    public static ProcessSample S(int identity, float cpu = 0, float gpu = 0, long io = 0, long net = 0, long disk = 0, long ws = 0, int faults = 0, long gpuMem = 0) =>
        new() { Pid = identity + 1, IdentityId = identity, CpuPercent = cpu, GpuPercent = gpu, ReadBytes = io, NetBytes = net, DiskReadBytes = disk, WorkingSet = ws, HardFaults = faults, GpuMemoryBytes = gpuMem };

    /// <summary>n flat ticks (cpu 10 %) from unix 1000, spaced <paramref name="intervalSeconds"/> apart, with a.exe at 5 % CPU in each.</summary>
    public static List<Tick> Flat(int n, float cpu = 10, int intervalSeconds = 1) =>
        Enumerable.Range(0, n).Select(i => TickAt(1000 + i * intervalSeconds, intervalSeconds, cpu, S(0, cpu: cpu / 2))).ToList();
}
