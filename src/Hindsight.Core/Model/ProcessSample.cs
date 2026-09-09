namespace Hindsight.Core.Model;

/// <summary>One process's resource use during a single tick. Mutable by design: samples live in arrays and are written in place.</summary>
public struct ProcessSample
{
    public int Pid;
    public int IdentityId;
    public float CpuPercent;
    public long ReadBytes;
    public long WriteBytes;
    public long NetBytes;
    public long WorkingSet;
    public SampleFlags Flags;
    public float GpuPercent;
    public long GpuMemoryBytes;
    public long PrivateBytes;
    public int HandleCount;
    public int ThreadCount;
    public int HardFaults;
    public long DiskReadBytes;
    public long DiskWriteBytes;
}
