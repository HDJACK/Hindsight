using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Hindsight.Core.Model;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>Reads path, command line, and start time of a process. All best effort, never throws.</summary>
public sealed class ProcessInfoReader
{
    private readonly Func<bool> _storeCommandLines;

    public ProcessInfoReader(Func<bool>? storeCommandLines = null) => _storeCommandLines = storeCommandLines ?? (() => true);

    public ProcessIdentity GetIdentity(int pid, long startTime, string name, int parentPid)
    {
        string path = "", cmd = "";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                path = ReadImagePath(h);
                if (_storeCommandLines()) cmd = ReadCommandLine(h);
            }
            finally { CloseHandle(h); }
        }
        if (!string.IsNullOrEmpty(path) && (string.IsNullOrEmpty(name) || name.StartsWith("PID ")))
            name = System.IO.Path.GetFileName(path);
        var (description, company) = path.Length != 0 ? ReadVersionInfo(path) : ("", "");
        return new ProcessIdentity(pid, startTime, name, path, cmd, parentPid, Description: description, Company: company);
    }

    private const int VersionCacheLimit = 2000;
    private static readonly ConcurrentDictionary<string, (string Description, string Company)> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File description and company of an image, cached per path. ("", "") when the file is missing or unreadable.</summary>
    public static (string Description, string Company) ReadVersionInfo(string path)
    {
        if (string.IsNullOrEmpty(path)) return ("", "");
        if (VersionCache.TryGetValue(path, out var cached)) return cached;
        (string, string) info;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            info = (v.FileDescription ?? "", v.CompanyName ?? "");
        }
        catch (Exception)
        {
            // The class contract is "never throws": any failure reading version info (missing file,
            // access denied, malformed resource, etc.) is cached as ("", "") deliberately.
            info = ("", "");
        }
        // The cache is a bounded convenience, not a correctness requirement: drop it wholesale when
        // it grows past the limit rather than tracking per-entry age.
        if (VersionCache.Count >= VersionCacheLimit) VersionCache.Clear();
        VersionCache[path] = info;
        return info;
    }

    public long? GetCreateTime(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(h, out long creation, out _, out _, out _) ? creation : null;
        }
        finally { CloseHandle(h); }
    }

    private static string ReadImagePath(IntPtr h)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
    }

    private static string ReadCommandLine(IntPtr h)
    {
        const int size = 64 * 1024;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out _);
            if (status != 0) return "";
            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return "";
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2) ?? "";
        }
        catch { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
