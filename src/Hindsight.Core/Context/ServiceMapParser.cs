namespace Hindsight.Core.Context;

/// <summary>Turns a flat list of (service name, hosting pid) pairs into one display string per pid.</summary>
public static class ServiceMapParser
{
    /// <summary>pid → "A, B" (sorted ordinal-ignore-case, distinct); pid 0 skipped; empty names skipped.</summary>
    public static IReadOnlyDictionary<int, string> Build(IEnumerable<(string Name, int Pid)> services)
    {
        var byPid = new Dictionary<int, SortedSet<string>>();
        foreach (var (name, pid) in services)
        {
            if (pid == 0 || string.IsNullOrEmpty(name)) continue;
            if (!byPid.TryGetValue(pid, out var set)) byPid[pid] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(name);
        }
        var result = new Dictionary<int, string>(byPid.Count);
        foreach (var (pid, set) in byPid) result[pid] = string.Join(", ", set);
        return result;
    }
}
