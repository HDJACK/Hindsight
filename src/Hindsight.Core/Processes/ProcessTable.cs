using Hindsight.Core.Model;

namespace Hindsight.Core.Processes;

/// <summary>Live processes. PID reuse is detected via StartTime.</summary>
public sealed class ProcessTable
{
    private readonly Dictionary<int, ProcessIdentity> _byPid = new();
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _byPid.Count; } }
    public IReadOnlyCollection<ProcessIdentity> All { get { lock (_lock) return _byPid.Values.ToArray(); } }

    public ProcessIdentity? Get(int pid, long startTime)
    {
        lock (_lock) return _byPid.TryGetValue(pid, out var id) && id.StartTime == startTime ? id : null;
    }

    public ProcessIdentity? GetByPid(int pid)
    {
        lock (_lock) return _byPid.TryGetValue(pid, out var id) ? id : null;
    }

    public void Upsert(ProcessIdentity identity)
    {
        lock (_lock)
        {
            if (_byPid.TryGetValue(identity.Pid, out var existing) && existing.StartTime == identity.StartTime)
            {
                _byPid[identity.Pid] = existing with
                {
                    Name = string.IsNullOrEmpty(existing.Name) || existing.Name.StartsWith("PID ") ? identity.Name : existing.Name,
                    Path = string.IsNullOrEmpty(existing.Path) ? identity.Path : existing.Path,
                    CommandLine = string.IsNullOrEmpty(existing.CommandLine) ? identity.CommandLine : existing.CommandLine,
                    ParentPid = existing.ParentPid == 0 ? identity.ParentPid : existing.ParentPid,
                    Services = string.IsNullOrEmpty(identity.Services) ? existing.Services : identity.Services,
                    Description = string.IsNullOrEmpty(identity.Description) ? existing.Description : identity.Description,
                    Company = string.IsNullOrEmpty(identity.Company) ? existing.Company : identity.Company,
                };
                return;
            }
            _byPid[identity.Pid] = identity;
        }
    }

    public bool Remove(int pid, long startTime)
    {
        lock (_lock)
        {
            if (_byPid.TryGetValue(pid, out var id) && id.StartTime == startTime) return _byPid.Remove(pid);
            return false;
        }
    }

    /// <summary>Sets Services on the current entry for pid. Returns the updated identity when it changed, null when unchanged/unknown.</summary>
    public ProcessIdentity? SetServices(int pid, string services)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(pid, out var existing)) return null;
            if (existing.Services == services) return null;
            var updated = existing with { Services = services };
            _byPid[pid] = updated;
            return updated;
        }
    }
}
