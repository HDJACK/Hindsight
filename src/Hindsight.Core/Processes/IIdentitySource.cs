using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Processes;

/// <summary>Resolves identity ids (as stored in ticks) and pids to identities. Live and recorded data both implement it.</summary>
public interface IIdentitySource
{
    ProcessIdentity? Lookup(int identityId);
    ProcessIdentity? ByPid(int pid);
}

/// <summary>Identity source for live data, backed by the interned identity store and the live process table.</summary>
public sealed class LiveIdentitySource : IIdentitySource
{
    private readonly IdentityStore _store;
    private readonly ProcessTable _table;
    public LiveIdentitySource(IdentityStore store, ProcessTable table) { _store = store; _table = table; }
    public ProcessIdentity? Lookup(int identityId) => _store.Lookup(identityId);
    public ProcessIdentity? ByPid(int pid) => _table.GetByPid(pid);
}
