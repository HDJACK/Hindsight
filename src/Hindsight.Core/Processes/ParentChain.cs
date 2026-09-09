using Hindsight.Core.Model;

namespace Hindsight.Core.Processes;

/// <summary>Renders a process's ancestry as a readable chain.</summary>
public static class ParentChain
{
    /// <summary>Returns e.g. "explorer.exe > cmd.exe > x.exe" with at most maxDepth parents.</summary>
    public static string Describe(ProcessIdentity identity, ProcessTable table, int maxDepth = 3) =>
        Describe(identity, table.GetByPid, maxDepth);

    public static string Describe(ProcessIdentity identity, IIdentitySource source, int maxDepth = 3) =>
        Describe(identity, source.ByPid, maxDepth);

    private static string Describe(ProcessIdentity identity, Func<int, ProcessIdentity?> byPid, int maxDepth)
    {
        var parts = new List<string> { identity.Name };
        int parent = identity.ParentPid;
        var seen = new HashSet<int> { identity.Pid };
        for (int depth = 0; depth < maxDepth && parent != 0 && seen.Add(parent); depth++)
        {
            var p = byPid(parent);
            if (p is null) { parts.Add($"PID {parent}"); break; }
            parts.Add(p.Name);
            parent = p.ParentPid;
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }
}
