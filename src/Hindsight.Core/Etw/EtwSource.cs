using Hindsight.Core.Context;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using Hindsight.Core.Sources;
using Hindsight.Core.Win32;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Hindsight.Core.Etw;

/// <summary>
/// ETW-based sample source. Aggregates per-process file I/O (FileIOInit), physical disk I/O
/// (DiskIO), and TCP/UDP network traffic (NetworkTCPIP) between calls to <see cref="Read"/>,
/// and reports process start/exit via the Kernel-Process provider.
/// Thread and Process keywords are also enabled so TraceEvent can attribute DiskIO events to
/// the issuing process via its thread table.
/// </summary>
public sealed class EtwSource : ISampleSource
{
    public const string SessionName = "Hindsight";
    private const string KernelProcessProvider = "Microsoft-Windows-Kernel-Process";
    private const string DnsClientProvider = "Microsoft-Windows-DNS-Client";
    private const int DnsQueryCompletedEventId = 3006;
    private const ulong KeywordProcess = 0x10;

    // Reading AppSettings.Default calls Environment.GetFolderPath to compute DataFolder; computing
    // it once here means the default settings delegate is a cheap field read, not per-event I/O.
    private static readonly AppSettings DefaultSettings = AppSettings.Default;

    public string Name => "etw";

    private sealed class Acc { public long Read, Write, Net, DiskRead, DiskWrite; }

    private readonly object _lock = new();
    private readonly Dictionary<int, Acc> _acc = new();
    private readonly List<ProcessIdentity> _started = new();
    private readonly List<ProcessExit> _exited = new();
    private readonly Dictionary<int, ProcessIdentity> _known = new();
    private readonly ProcessInfoReader _reader;
    private readonly ContextAccumulator? _context;
    private readonly Func<AppSettings> _settings;
    private readonly OverloadGuard _guard = new();
    private TraceEventSession? _session;
    private Task? _pump;
    private int _lastLost;
    private int _callbackErrors;
    private double _cyclesPerSecond = 3e9;

    /// <summary>True once the overload guard has tripped: file-path context capture stops for the rest of the session.</summary>
    public bool FilePathsDisabled { get; private set; }

    /// <summary>True when the DNS-Client provider could not be enabled; the session still runs without DNS names.</summary>
    public bool DnsUnavailable { get; private set; }

    /// <summary>How many ETW callbacks threw and were swallowed. An exception escaping a callback would kill the
    /// pump thread and end the session silently, so every callback body is guarded; this counter is the only trace.
    /// Core does not log, so the App reads it if it wants to surface the number.</summary>
    public int CallbackErrors => Volatile.Read(ref _callbackErrors);

    /// <summary>Raised once, from the Read() thread, when the overload guard disables file-path capture.</summary>
    public event Action? FilePathsDisabledByOverload;

    public EtwSource(ProcessInfoReader? reader = null, ContextAccumulator? context = null, Func<AppSettings>? settings = null)
    {
        _reader = reader ?? new ProcessInfoReader();
        _context = context;
        _settings = settings ?? (() => DefaultSettings);
    }

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
            throw new EtwUnavailableException("Administrator rights are required");
        _cyclesPerSecond = CycleCalibrator.CyclesPerSecond();
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.DiskIO
                | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.Process);
            _session.EnableProvider(KernelProcessProvider, TraceEventLevel.Informational, KeywordProcess);
        }
        catch (Exception ex)
        {
            _session?.Dispose();
            _session = null;
            throw new EtwUnavailableException("ETW session could not be started: " + ex.Message, ex);
        }

        try
        {
            _session.EnableProvider(DnsClientProvider, TraceEventLevel.Informational);
        }
        catch (Exception)
        {
            // The DNS-Client provider is optional: without it the session runs without DNS names.
            DnsUnavailable = true;
        }

        var k = _session.Source.Kernel;
        k.FileIORead += d => OnFileIo(d, isRead: true);
        k.FileIOWrite += d => OnFileIo(d, isRead: false);
        k.DiskIORead += d => OnDiskIo(d, isRead: true);
        k.DiskIOWrite += d => OnDiskIo(d, isRead: false);
        k.TcpIpSend += OnTcpSend;
        k.TcpIpRecv += OnTcpRecv;
        k.TcpIpSendIPV6 += OnTcpSendV6;
        k.TcpIpRecvIPV6 += OnTcpRecvV6;
        k.UdpIpSend += OnUdp;
        k.UdpIpRecv += OnUdp;
        k.UdpIpSendIPV6 += OnUdpV6;
        k.UdpIpRecvIPV6 += OnUdpV6;
        _session.Source.Dynamic.All += OnDynamic;

        _pump = Task.Factory.StartNew(() => _session.Source.Process(), TaskCreationOptions.LongRunning);
    }

    private void AddIo(int pid, long read, long write)
    {
        if (pid <= 0) return;
        lock (_lock)
        {
            if (!_acc.TryGetValue(pid, out var a)) _acc[pid] = a = new Acc();
            a.Read += read; a.Write += write;
        }
    }

    private void AddDisk(int pid, long read, long write)
    {
        if (pid <= 0) return;
        lock (_lock)
        {
            if (!_acc.TryGetValue(pid, out var a)) _acc[pid] = a = new Acc();
            a.DiskRead += read; a.DiskWrite += write;
        }
    }

    private void OnFileIo(FileIOReadWriteTraceData d, bool isRead)
    {
        try
        {
            long size = d.IoSize;
            AddIo(d.ProcessID, isRead ? size : 0, isRead ? 0 : size);
            AddFile(d, size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnDiskIo(DiskIOTraceData d, bool isRead)
    {
        try
        {
            long size = d.TransferSize;
            AddDisk(d.ProcessID, isRead ? size : 0, isRead ? 0 : size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    /// <summary>Caller guards: d.FileName resolves the kernel's file-object table and can throw when the mapping is missing.</summary>
    private void AddFile(FileIOReadWriteTraceData d, long size)
    {
        if (_context is null || !_settings().CaptureFilePaths || FilePathsDisabled) return;
        string n = d.FileName;
        if (n.Length > 0) _context.Add(d.ProcessID, ContextKind.File, n, size);
    }

    // Every OnTcp*/OnUdp* handler below checks this before touching d.daddr/d.dport: materialising daddr
    // allocates a fresh IPAddress and parses the raw payload, which can throw on a truncated event.
    private bool EndpointsEnabled => _context != null && _settings().CaptureDnsAndEndpoints;

    private void OnTcpSend(TcpIpSendTraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnTcpRecv(TcpIpTraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnTcpSendV6(TcpIpV6SendTraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnTcpRecvV6(TcpIpV6TraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnUdp(UdpIpTraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnUdpV6(UpdIpV6TraceData d)
    {
        try
        {
            AddNet(d.ProcessID, d.size);
            if (EndpointsEnabled) _context!.Add(d.ProcessID, ContextKind.Endpoint, $"{d.daddr}:{d.dport}", d.size);
        }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void AddNet(int pid, long bytes)
    {
        if (pid <= 0) return;
        lock (_lock)
        {
            if (!_acc.TryGetValue(pid, out var a)) _acc[pid] = a = new Acc();
            a.Net += bytes;
        }
    }

    private void OnDynamic(TraceEvent e)
    {
        // Reading the payload can throw, and anything escaping here unwinds the pump thread.
        try { OnDynamicCore(e); }
        catch { Interlocked.Increment(ref _callbackErrors); }
    }

    private void OnDynamicCore(TraceEvent e)
    {
        if (e.ProviderName == DnsClientProvider)
        {
            if ((int)e.ID == DnsQueryCompletedEventId && _context != null && _settings().CaptureDnsAndEndpoints
                && e.PayloadByName("QueryName") is string q && q.Length > 0)
                _context.Add(e.ProcessID, ContextKind.Dns, q, 1);
            return;
        }
        if (e.ProviderName != KernelProcessProvider) return;
        int id = (int)e.ID;
        if (id != 1 && id != 2) return;
        try
        {
            int pid = Convert.ToInt32(e.PayloadByName("ProcessID"));
            string image = e.PayloadByName("ImageName") as string ?? "";
            string name = image.Length == 0 ? $"PID {pid}" : System.IO.Path.GetFileName(image);
            long payloadCreate = e.PayloadByName("CreateTime") is DateTime dt
                ? (dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime()).ToFileTimeUtc()
                : 0;

            if (id == 1)
            {
                int parent = Convert.ToInt32(e.PayloadByName("ParentProcessID"));
                long start = _reader.GetCreateTime(pid) ?? payloadCreate;
                var identity = _reader.GetIdentity(pid, start, name, parent);
                lock (_lock) { _started.Add(identity); _known[pid] = identity; }
            }
            else
            {
                ulong cycles = Convert.ToUInt64(e.PayloadByName("CPUCycleCount"));
                long readKb = Convert.ToInt64(e.PayloadByName("ReadTransferKiloBytes"));
                long writeKb = Convert.ToInt64(e.PayloadByName("WriteTransferKiloBytes"));
                lock (_lock)
                {
                    if (!_known.Remove(pid, out var identity))
                        identity = new ProcessIdentity(pid, payloadCreate, name, image, "", 0);
                    _exited.Add(new ProcessExit(identity, cycles / _cyclesPerSecond, readKb * 1024, writeKb * 1024));
                }
            }
        }
        catch
        {
            // Payload layout differs on this Windows version: ignore the event
        }
    }

    public SourceSnapshot Read()
    {
        var snap = new SourceSnapshot();
        lock (_lock)
        {
            foreach (var (pid, a) in _acc)
                snap.Deltas.Add(new ProcessDelta(pid, 0, 0, a.Read, a.Write, a.Net, 0, DiskReadBytes: a.DiskRead, DiskWriteBytes: a.DiskWrite));
            snap.Started.AddRange(_started);
            snap.Exited.AddRange(_exited);
            _acc.Clear(); _started.Clear(); _exited.Clear();
        }
        if (_session is not null)
        {
            // ETWTraceEventSource.EventsLost is a one-time snapshot taken when the source
            // was created and never updates for real-time sessions; the session itself
            // tracks the live count.
            int lost = _session.EventsLost;
            snap.LostEvents = Math.Max(0, lost - _lastLost);
            _lastLost = lost;
            if (_guard.Observe(snap.LostEvents))
            {
                FilePathsDisabled = true;
                FilePathsDisabledByOverload?.Invoke();
            }
        }
        return snap;
    }

    public void Dispose()
    {
        try { _session?.Source.StopProcessing(); } catch { }
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
    }
}
