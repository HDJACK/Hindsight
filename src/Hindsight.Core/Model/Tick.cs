namespace Hindsight.Core.Model;

/// <summary>One recorded second: machine-wide readings plus the most interesting process samples of that second.</summary>
public sealed class Tick
{
    public const int MaxSamples = 40;

    public long UnixTime;
    public float CpuTotalPercent;
    public long DiskBytes;
    public long NetBytes;
    public long RamAvailableBytes;
    public int ProcessCount;
    public int LostEvents;
    public TickFlags Flags;
    public byte IntervalSeconds = 1;
    public float GpuTotalPercent;
    public ushort CpuMhz;
    public float DiskQueue;
    public float DiskLatencyMs;
    public float CommitPercent;
    public uint HardFaultsPerSec;
    public sbyte BatteryPercent = -1;
    public int ForegroundPid;
    public ProcessSample[] Samples = Array.Empty<ProcessSample>();

    public bool Has(TickFlags f) => (Flags & f) != 0;

    public Tick Copy() => new()
    {
        UnixTime = UnixTime, CpuTotalPercent = CpuTotalPercent, DiskBytes = DiskBytes, NetBytes = NetBytes,
        RamAvailableBytes = RamAvailableBytes, ProcessCount = ProcessCount, LostEvents = LostEvents,
        Flags = Flags, IntervalSeconds = IntervalSeconds,
        GpuTotalPercent = GpuTotalPercent, CpuMhz = CpuMhz, DiskQueue = DiskQueue, DiskLatencyMs = DiskLatencyMs,
        CommitPercent = CommitPercent, HardFaultsPerSec = HardFaultsPerSec, BatteryPercent = BatteryPercent,
        ForegroundPid = ForegroundPid, Samples = (ProcessSample[])Samples.Clone(),
    };
}
