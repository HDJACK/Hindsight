using System.Runtime.InteropServices;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>
/// Thin wrapper around a single PDH query handle plus the counters added to it.
/// </summary>
public sealed class Pdh : IDisposable
{
    private IntPtr _query;

    private Pdh(IntPtr query) => _query = query;

    public static bool TryOpen(out Pdh? pdh)
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out IntPtr query);
        if (status != 0 || query == IntPtr.Zero)
        {
            pdh = null;
            return false;
        }
        pdh = new Pdh(query);
        return true;
    }

    /// <summary>Adds an English counter path. Returns null when PDH rejects the counter (e.g. not present on this machine).</summary>
    public PdhCounter? AddCounter(string englishPath)
    {
        uint status = PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr counter);
        if (status != 0 || counter == IntPtr.Zero) return null;
        return new PdhCounter(counter, englishPath);
    }

    public bool Collect() => PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } finally { _query = IntPtr.Zero; }
        }
    }
}

/// <summary>A single counter on a <see cref="Pdh"/> query, read either as one value or as an array of instances.</summary>
public sealed class PdhCounter
{
    private readonly IntPtr _handle;

    public string Path { get; }

    internal PdhCounter(IntPtr handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public bool TryGetDouble(out double value)
    {
        uint status = PdhGetFormattedCounterValue(_handle, PDH_FMT_DOUBLE, IntPtr.Zero, out var fmt);
        if (status != 0 || (fmt.CStatus != PDH_CSTATUS_VALID_DATA && fmt.CStatus != PDH_CSTATUS_NEW_DATA))
        {
            value = 0;
            return false;
        }
        value = fmt.doubleValue;
        return true;
    }

    public List<(string name, double value)> GetArray()
    {
        var result = new List<(string, double)>();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint bufferSize = 0;
            uint itemCount = 0;
            uint status = PdhGetFormattedCounterArrayW(_handle, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, IntPtr.Zero);
            if (status != PDH_MORE_DATA || bufferSize == 0) return result;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
            try
            {
                status = PdhGetFormattedCounterArrayW(_handle, PDH_FMT_DOUBLE, ref bufferSize, ref itemCount, buffer);
                if (status == PDH_MORE_DATA)
                {
                    // Instance set grew between the sizing call and the data call; retry with the new size.
                    continue;
                }
                if (status != 0) return result;

                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_W>();
                for (int i = 0; i < itemCount; i++)
                {
                    IntPtr itemPtr = buffer + i * itemSize;
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_W>(itemPtr);
                    if (item.FmtValue.CStatus != PDH_CSTATUS_VALID_DATA && item.FmtValue.CStatus != PDH_CSTATUS_NEW_DATA) continue;
                    string name = item.szName != IntPtr.Zero ? (Marshal.PtrToStringUni(item.szName) ?? "") : "";
                    result.Add((name, item.FmtValue.doubleValue));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }
}
