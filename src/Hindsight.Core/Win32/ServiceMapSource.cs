using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Hindsight.Core.Context;
using Hindsight.Core.Sources;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>Maps hosting PIDs to the Windows services they run, refreshed on a slow cadence (services rarely move).</summary>
public sealed class ServiceMapSource : ISampleSource
{
    public string Name => "services";

    private readonly TimeSpan _refresh;
    private readonly Stopwatch _since = new();
    private bool _enumeratedOnce;

    public ServiceMapSource(TimeSpan? refresh = null) => _refresh = refresh ?? TimeSpan.FromSeconds(10);

    /// <summary>Win32 message of the last failed enumeration, null when the last enumeration succeeded.</summary>
    public string? LastError { get; private set; }

    /// <summary>Resets the enumerated-once flag and restarts the interval so, after a stop/start cycle,
    /// the next <see cref="Read"/> enumerates immediately rather than waiting a full refresh interval.</summary>
    public void Start()
    {
        _enumeratedOnce = false;
        _since.Restart();
    }

    public SourceSnapshot Read()
    {
        var snap = new SourceSnapshot();
        if (_enumeratedOnce && _since.Elapsed < _refresh) return snap;
        _enumeratedOnce = true;
        _since.Restart();
        snap.Services = Enumerate();
        return snap;
    }

    private IReadOnlyDictionary<int, string>? Enumerate()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int resume = 0;
            if (EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0,
                    out int needed, out _, ref resume, null))
            {
                // No services at all: nothing to map, but the call itself succeeded.
                LastError = null;
                return ServiceMapParser.Build(Array.Empty<(string, int)>());
            }
            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_MORE_DATA || needed <= 0)
            {
                LastError = new Win32Exception(err).Message;
                return null;
            }

            buffer = Marshal.AllocHGlobal(needed);
            resume = 0;
            if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buffer, needed,
                    out _, out int returned, ref resume, null))
            {
                LastError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            int size = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            var list = new List<(string Name, int Pid)>(returned);
            for (int i = 0; i < returned; i++)
            {
                var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(IntPtr.Add(buffer, i * size));
                string name = e.ServiceName != IntPtr.Zero ? Marshal.PtrToStringUni(e.ServiceName) ?? "" : "";
                list.Add((name, (int)e.Status.ProcessId));
            }
            LastError = null;
            return ServiceMapParser.Build(list);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
    }

    public void Dispose() => _since.Stop();
}
