using Hindsight.Core.Context;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Recordings;

/// <summary>A fully loaded .hsrec file. Identity ids in Ticks index Identities directly.</summary>
public sealed class Recording : IIdentitySource, IContextSource
{
    public RecordingMeta Meta { get; }
    public IReadOnlyList<Tick> Ticks { get; }
    public IReadOnlyList<ProcessIdentity> Identities { get; }
    public IReadOnlyList<Marker> Markers { get; }
    public IReadOnlyList<ContextEntry> Context { get; }
    private readonly Dictionary<int, ProcessIdentity> _byPid = new();
    private readonly Dictionary<(long, int), List<ContextEntry>> _byMinutePid = new();

    public Recording(RecordingMeta meta, IReadOnlyList<Tick> ticks, IReadOnlyList<ProcessIdentity> identities, IReadOnlyList<Marker> markers,
        IReadOnlyList<ContextEntry>? context = null)
    {
        Meta = meta; Ticks = ticks; Identities = identities; Markers = markers;
        Context = context ?? Array.Empty<ContextEntry>();
        foreach (var id in identities) _byPid[id.Pid] = id;   // last one wins
        foreach (var e in Context)
        {
            var key = (e.Minute, e.Pid);
            if (!_byMinutePid.TryGetValue(key, out var list)) _byMinutePid[key] = list = new List<ContextEntry>();
            list.Add(e);
        }
        foreach (var list in _byMinutePid.Values)
            list.Sort((a, b) =>
            {
                int c = a.Kind.CompareTo(b.Kind);
                return c != 0 ? c : b.Value.CompareTo(a.Value);
            });
    }

    public ProcessIdentity? Lookup(int identityId) => identityId >= 0 && identityId < Identities.Count ? Identities[identityId] : null;
    public ProcessIdentity? ByPid(int pid) => _byPid.TryGetValue(pid, out var v) ? v : null;

    public bool HasContext => Meta.FormatVersion >= 3;

    public IReadOnlyList<ContextEntry> ForMinute(long minute, int pid) =>
        _byMinutePid.TryGetValue((minute, pid), out var list) ? list : Array.Empty<ContextEntry>();
}
