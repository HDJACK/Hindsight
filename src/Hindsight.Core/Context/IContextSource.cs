using Hindsight.Core.Model;

namespace Hindsight.Core.Context;

/// <summary>Supplies the per-minute context (DNS names, endpoints, file paths) recorded alongside ticks.</summary>
public interface IContextSource
{
    /// <summary>False for sources that never recorded context (v1/v2 files).</summary>
    bool HasContext { get; }

    /// <summary>Entries for one minute bucket and pid, sorted by Kind, then Value desc.</summary>
    IReadOnlyList<ContextEntry> ForMinute(long minute, int pid);
}
