namespace Hindsight.Core.Model;

/// <summary>How a process sample was obtained.</summary>
[Flags] public enum SampleFlags : byte { None = 0, ShortLived = 1, Estimated = 2 }
/// <summary>What was true of the machine, or of the recording itself, during a tick.</summary>
[Flags] public enum TickFlags : byte { None = 0, Spike = 1, EtwActive = 2, Gap = 4, OnAc = 8, UserIdle = 16 }
/// <summary>What a marker on the timeline stands for.</summary>
public enum MarkerKind { Manual, AcConnected, AcDisconnected, Suspend, Resume, Lock, Unlock,
    WindowsUpdate, DefenderScan, ScheduledTask, UsbArrived, UsbRemoved, DisplayOff, DisplayOn }
/// <summary>How much detail the UI shows.</summary>
public enum DetailLevel { Easy, Professional }
/// <summary>One row of the timeline graph.</summary>
public enum LaneKind { Cpu, Gpu, FileIo, DiskPhysical, Network, RamFree, DiskLatency, CpuMhz, Commit }
/// <summary>How far from its baseline a value must stray before it counts as an anomaly.</summary>
public enum AnomalySensitivity { Low, Normal, High }
/// <summary>The kind of context observation recorded for a process.</summary>
public enum ContextKind { Dns, Endpoint, File }
