using System.Runtime.InteropServices;
using Hindsight.Core.Model;
using Hindsight.Core.Sources;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>Polls the whole process list for CPU, memory, handle and thread counts, and reports the system CPU and RAM totals.</summary>
public sealed class SnapshotSource : ISampleSource
{
    public string Name => "snapshot";

    private readonly ProcessInfoReader _reader;
    private readonly Dictionary<int, (long StartTime, long Cpu100ns, uint HardFaults)> _prev = new();

    public SnapshotSource(ProcessInfoReader? reader = null) => _reader = reader ?? new ProcessInfoReader();
    private IntPtr _buffer;
    private int _bufferSize = 1 << 20;
    private long _prevIdle, _prevKernel, _prevUser;
    private bool _haveSystemTimes;

    public void Start()
    {
        _buffer = Marshal.AllocHGlobal(_bufferSize);
        if (GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser)) _haveSystemTimes = true;
    }

    public SourceSnapshot Read()
    {
        if (_buffer == IntPtr.Zero) throw new InvalidOperationException("SnapshotSource.Read() called before Start() or after Dispose()");
        var snap = new SourceSnapshot();
        QueryProcesses();

        var seen = new HashSet<int>();
        int count = 0;
        IntPtr p = _buffer;
        while (true)
        {
            var info = Marshal.PtrToStructure<SYSTEM_PROCESS_INFORMATION>(p);
            int pid = (int)info.UniqueProcessId;
            if (pid != 0)
            {
                count++;
                seen.Add(pid);
                long cpu = info.KernelTime + info.UserTime;
                string name = info.ImageName.Buffer != IntPtr.Zero && info.ImageName.Length > 0
                    ? Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2) ?? ""
                    : (pid == 4 ? "System" : $"PID {pid}");
                long ws = (long)info.WorkingSetSize;
                long privateBytes = (long)info.PagefileUsage;
                int handleCount = (int)info.HandleCount;
                int threadCount = (int)info.NumberOfThreads;

                if (_prev.TryGetValue(pid, out var prev) && prev.StartTime == info.CreateTime)
                {
                    double cpuSeconds = Math.Max(0, cpu - prev.Cpu100ns) / 1e7;
                    int hardFaults = info.HardFaultCount >= prev.HardFaults ? (int)Math.Min(int.MaxValue, info.HardFaultCount - prev.HardFaults) : 0;
                    snap.Deltas.Add(new ProcessDelta(pid, info.CreateTime, cpuSeconds, 0, 0, 0, ws,
                        PrivateBytes: privateBytes, HandleCount: handleCount, ThreadCount: threadCount, HardFaults: hardFaults));
                }
                else
                {
                    snap.Started.Add(_reader.GetIdentity(pid, info.CreateTime, name, (int)info.InheritedFromUniqueProcessId));
                    snap.Deltas.Add(new ProcessDelta(pid, info.CreateTime, 0, 0, 0, 0, ws,
                        PrivateBytes: privateBytes, HandleCount: handleCount, ThreadCount: threadCount, HardFaults: 0));
                }
                _prev[pid] = (info.CreateTime, cpu, info.HardFaultCount);
            }
            if (info.NextEntryOffset == 0) break;
            p += (int)info.NextEntryOffset;
        }

        foreach (var gone in _prev.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            long prevStartTime = _prev[gone].StartTime;
            snap.Exited.Add(new ProcessExit(new ProcessIdentity(gone, prevStartTime, $"PID {gone}", "", "", 0), 0, 0, 0));
            _prev.Remove(gone);
        }

        var (avail, total) = ReadRam();
        snap.Totals = new SystemTotals(ReadCpuPercent(), avail, count, RamTotalBytes: total);
        return snap;
    }

    private void QueryProcesses()
    {
        while (true)
        {
            uint status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int needed);
            if (status == 0) return;
            if (status != STATUS_INFO_LENGTH_MISMATCH)
                throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            _bufferSize = Math.Max(needed + (64 << 10), _bufferSize * 2);
            _buffer = Marshal.ReAllocHGlobal(_buffer, (IntPtr)_bufferSize);
        }
    }

    private double ReadCpuPercent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        if (!_haveSystemTimes) { (_prevIdle, _prevKernel, _prevUser, _haveSystemTimes) = (idle, kernel, user, true); return 0; }
        long total = (kernel - _prevKernel) + (user - _prevUser);
        long busy = total - (idle - _prevIdle);
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        return total <= 0 ? 0 : Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static (long avail, long total) ReadRam()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref s) ? ((long)s.ullAvailPhys, (long)s.ullTotalPhys) : (0, 0);
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }
}
