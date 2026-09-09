using Hindsight.Core.Model;
using Hindsight.Core.Sources;
using Microsoft.Win32;

namespace Hindsight.Core.Win32;

/// <summary>
/// PDH-based sample source: GPU utilization/memory per process, CPU clock speed,
/// disk queue/latency, and memory pressure (commit percent, hard faults/sec).
/// </summary>
public sealed class PdhSource : ISampleSource
{
    private const string GpuUtilizationPath = @"\GPU Engine(*)\Utilization Percentage";
    private const string GpuMemoryPath = @"\GPU Process Memory(*)\Dedicated Usage";
    private const string CpuPerformancePath = @"\Processor Information(_Total)\% Processor Performance";
    private const string DiskQueuePath = @"\PhysicalDisk(_Total)\Current Disk Queue Length";
    private const string DiskLatencyPath = @"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer";
    private const string CommitPercentPath = @"\Memory\% Committed Bytes In Use";
    private const string HardFaultsPath = @"\Memory\Page Reads/sec";

    public string Name => "pdh";

    public IReadOnlyList<string> MissingCounters => _missingCounters;

    private readonly List<string> _missingCounters = new();
    private Pdh? _pdh;
    private PdhCounter? _gpuUtilization;
    private PdhCounter? _gpuMemory;
    private PdhCounter? _cpuPerformance;
    private PdhCounter? _diskQueue;
    private PdhCounter? _diskLatency;
    private PdhCounter? _commitPercent;
    private PdhCounter? _hardFaults;
    private ushort _baseMhz;
    private bool _started;

    public void Start()
    {
        _baseMhz = ReadBaseMhz();

        if (!Pdh.TryOpen(out _pdh) || _pdh is null)
        {
            _missingCounters.Add(GpuUtilizationPath);
            _missingCounters.Add(GpuMemoryPath);
            _missingCounters.Add(CpuPerformancePath);
            _missingCounters.Add(DiskQueuePath);
            _missingCounters.Add(DiskLatencyPath);
            _missingCounters.Add(CommitPercentPath);
            _missingCounters.Add(HardFaultsPath);
            _started = true;
            return;
        }

        _gpuUtilization = AddOrMarkMissing(GpuUtilizationPath);
        _gpuMemory = AddOrMarkMissing(GpuMemoryPath);
        _cpuPerformance = AddOrMarkMissing(CpuPerformancePath);
        _diskQueue = AddOrMarkMissing(DiskQueuePath);
        _diskLatency = AddOrMarkMissing(DiskLatencyPath);
        _commitPercent = AddOrMarkMissing(CommitPercentPath);
        _hardFaults = AddOrMarkMissing(HardFaultsPath);

        _started = true;
    }

    private PdhCounter? AddOrMarkMissing(string path)
    {
        var counter = _pdh!.AddCounter(path);
        if (counter is null) _missingCounters.Add(path);
        return counter;
    }

    public SourceSnapshot Read()
    {
        var snap = new SourceSnapshot();
        if (!_started || _pdh is null) return snap;

        _pdh.Collect();

        var gpuUtilValues = _gpuUtilization?.GetArray() ?? new List<(string, double)>();
        var gpuMemValues = _gpuMemory?.GetArray() ?? new List<(string, double)>();

        var gpuPercentByPid = GpuCounterParser.PerProcess(gpuUtilValues);
        var gpuMemByPid = GpuCounterParser.MemoryPerProcess(gpuMemValues);
        float gpuTotalPercent = GpuCounterParser.Total3D(gpuUtilValues);

        var pids = new HashSet<int>(gpuPercentByPid.Keys);
        pids.UnionWith(gpuMemByPid.Keys);
        foreach (int pid in pids)
        {
            gpuPercentByPid.TryGetValue(pid, out float gpuPercent);
            gpuMemByPid.TryGetValue(pid, out long gpuMem);
            snap.Deltas.Add(new ProcessDelta(pid, 0, 0, 0, 0, 0, 0, GpuPercent: gpuPercent, GpuMemoryBytes: gpuMem));
        }

        double cpuPerf = TryGetDoubleOrZero(_cpuPerformance);
        ushort cpuMhz = (ushort)Math.Clamp(cpuPerf / 100.0 * _baseMhz, 0, ushort.MaxValue);
        float diskQueue = (float)TryGetDoubleOrZero(_diskQueue);
        float diskLatencyMs = (float)(TryGetDoubleOrZero(_diskLatency) * 1000.0);
        float commitPercent = (float)TryGetDoubleOrZero(_commitPercent);
        uint hardFaultsPerSec = (uint)Math.Max(0, TryGetDoubleOrZero(_hardFaults));

        snap.Totals = new SystemTotals(0, 0, 0,
            GpuTotalPercent: gpuTotalPercent, CpuMhz: cpuMhz, DiskQueue: diskQueue, DiskLatencyMs: diskLatencyMs,
            CommitPercent: commitPercent, HardFaultsPerSec: hardFaultsPerSec);

        return snap;
    }

    private static double TryGetDoubleOrZero(PdhCounter? counter)
    {
        if (counter is null) return 0;
        return counter.TryGetDouble(out double value) ? value : 0;
    }

    private static ushort ReadBaseMhz()
    {
        try
        {
            object? value = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", 0);
            return value is null ? (ushort)0 : (ushort)Math.Clamp(Convert.ToInt64(value), 0, ushort.MaxValue);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        _pdh = null;
        _started = false;
    }
}
