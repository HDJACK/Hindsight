namespace Hindsight.Core.Win32;

/// <summary>
/// Parses PDH "GPU Engine"/"GPU Process Memory" instance names, e.g.
/// pid_1234_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D
/// </summary>
public static class GpuCounterParser
{
    private const string PidPrefix = "pid_";
    private const string EngTypeMarker = "_engtype_";

    public static bool TryParsePid(string instance, out int pid)
    {
        pid = 0;

        if (!instance.StartsWith(PidPrefix, StringComparison.Ordinal)) return false;

        int pidStart = PidPrefix.Length;
        int pidEnd = instance.IndexOf('_', pidStart);
        if (pidEnd < 0) return false;

        string pidStr = instance.Substring(pidStart, pidEnd - pidStart);
        if (!int.TryParse(pidStr, out pid)) { pid = 0; return false; }

        return true;
    }

    public static bool TryParseInstance(string instance, out int pid, out string engineType)
    {
        pid = 0;
        engineType = "";

        int engTypeIdx = instance.IndexOf(EngTypeMarker, StringComparison.Ordinal);
        if (engTypeIdx < 0) return false;

        if (!TryParsePid(instance, out pid)) return false;

        engineType = instance.Substring(engTypeIdx + EngTypeMarker.Length);
        if (engineType.Length == 0) { engineType = ""; pid = 0; return false; }
        return true;
    }

    public static Dictionary<int, float> PerProcess(IEnumerable<(string instance, double value)> values)
    {
        var result = new Dictionary<int, float>();
        foreach (var (instance, value) in values)
        {
            if (!TryParseInstance(instance, out int pid, out _)) continue;
            float v = (float)value;
            if (!result.TryGetValue(pid, out float existing) || v > existing) result[pid] = v;
        }
        return result;
    }

    public static float Total3D(IEnumerable<(string instance, double value)> values)
    {
        double sum = 0;
        foreach (var (instance, value) in values)
        {
            if (!TryParseInstance(instance, out _, out string engineType)) continue;
            if (engineType == "3D") sum += value;
        }
        return (float)Math.Min(100.0, sum);
    }

    public static Dictionary<int, long> MemoryPerProcess(IEnumerable<(string instance, double value)> values)
    {
        var result = new Dictionary<int, long>();
        foreach (var (instance, value) in values)
        {
            if (!TryParsePid(instance, out int pid)) continue;
            result[pid] = result.TryGetValue(pid, out long existing) ? existing + (long)value : (long)value;
        }
        return result;
    }
}
