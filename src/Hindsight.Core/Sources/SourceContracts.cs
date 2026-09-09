using Hindsight.Core.Model;

namespace Hindsight.Core.Sources;

/// <summary>What one process did since the last read, as reported by a single source.</summary>
public sealed record ProcessDelta(int Pid, long StartTime, double CpuSeconds, long ReadBytes, long WriteBytes, long NetBytes, long WorkingSet,
    float GpuPercent = 0, long GpuMemoryBytes = 0, long PrivateBytes = 0, int HandleCount = 0, int ThreadCount = 0, int HardFaults = 0,
    long DiskReadBytes = 0, long DiskWriteBytes = 0);

/// <summary>A process that ended, with whatever totals its source could still report for it.</summary>
public sealed record ProcessExit(ProcessIdentity Identity, double CpuSecondsEstimated, long ReadBytes, long WriteBytes);

/// <summary>One source's report for a single read: process deltas, starts and exits, plus any machine-wide totals it knows.</summary>
public sealed class SourceSnapshot
{
    public List<ProcessIdentity> Started { get; } = new();
    public List<ProcessDelta> Deltas { get; } = new();
    public List<ProcessExit> Exited { get; } = new();
    public SystemTotals Totals { get; set; }
    public int LostEvents { get; set; }

    /// <summary>pid → service names hosted by it, set only by the service source and only when its refresh is due.</summary>
    public IReadOnlyDictionary<int, string>? Services { get; set; }
}

/// <summary>A source of process and system measurements, read once per tick.</summary>
public interface ISampleSource : IDisposable
{
    string Name { get; }
    void Start();
    SourceSnapshot Read();
}
