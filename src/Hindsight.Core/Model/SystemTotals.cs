namespace Hindsight.Core.Model;

/// <summary>Machine-wide readings for a single tick, merged from every sample source.</summary>
public readonly record struct SystemTotals(double CpuPercent, long RamAvailableBytes, int ProcessCount, long DiskBytes = 0, long NetBytes = 0, long RamTotalBytes = 0,
    float GpuTotalPercent = 0, ushort CpuMhz = 0, float DiskQueue = 0, float DiskLatencyMs = 0, float CommitPercent = 0, uint HardFaultsPerSec = 0,
    sbyte BatteryPercent = -1, bool? OnAc = null, int ForegroundPid = 0, bool UserIdle = false);
