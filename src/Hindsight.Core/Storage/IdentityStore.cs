using System.Text.Json;
using Hindsight.Core.Model;

namespace Hindsight.Core.Storage;

/// <summary>JSON Lines file, appended to as identities are interned. Id = line index. Key is (Pid, StartTime).
/// <see cref="Update"/> rewrites the whole file so an id keeps its line.</summary>
public sealed class IdentityStore : IDisposable
{
    private readonly List<ProcessIdentity> _byId = new();
    private readonly Dictionary<(int, long), int> _ids = new();
    private readonly string? _path;
    private StreamWriter? _writer;
    private readonly object _lock = new();

    public IdentityStore(string? path)
    {
        _path = path;
        if (path is null) return;
        if (File.Exists(path))
        {
            foreach (var line in JsonLinesFile.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var id = JsonSerializer.Deserialize<ProcessIdentity>(line);
                    if (id is null)
                    {
                        // A corrupt line must still occupy a slot: id == line index is a
                        // contract every later line (and every stored IdentityId reference)
                        // relies on. Don't register the placeholder under a lookup key
                        // since we have no real (Pid, StartTime) for it.
                        _byId.Add(ProcessIdentity.Placeholder(-1, _byId.Count));
                        continue;
                    }
                    _ids[(id.Pid, id.StartTime)] = _byId.Count;
                    _byId.Add(id);
                }
                catch (JsonException)
                {
                    _byId.Add(ProcessIdentity.Placeholder(-1, _byId.Count));
                    continue;
                }
            }
        }
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public int Count { get { lock (_lock) return _byId.Count; } }

    public int Intern(ProcessIdentity identity)
    {
        lock (_lock)
        {
            if (_ids.TryGetValue((identity.Pid, identity.StartTime), out var existing)) return existing;
            int id = _byId.Count;
            _byId.Add(identity);
            _ids[(identity.Pid, identity.StartTime)] = id;
            _writer?.WriteLine(JsonSerializer.Serialize(identity));
            return id;
        }
    }

    /// <summary>Replaces the entry with the same (Pid, StartTime) and rewrites the file. No-op when the key is unknown.</summary>
    public void Update(ProcessIdentity identity) => UpdateMany(new[] { identity });

    /// <summary>Replaces every entry whose (Pid, StartTime) is known, rewriting the file once. Unknown
    /// identities are skipped; a no-op (including an empty rewrite) is skipped entirely.</summary>
    public void UpdateMany(IEnumerable<ProcessIdentity> identities)
    {
        lock (_lock)
        {
            bool any = false;
            foreach (var identity in identities)
            {
                if (!_ids.TryGetValue((identity.Pid, identity.StartTime), out var id)) continue;
                _byId[id] = identity;
                any = true;
            }
            if (!any || _path is null) return;
            // Rewrite in full: line index == id is the contract every stored IdentityId relies on,
            // so an updated identity must replace its own line rather than be appended. Do this once
            // per batch rather than once per identity to avoid O(n) full-file rewrites per advance.
            _writer?.Dispose();
            File.WriteAllLines(_path, _byId.Select(x => JsonSerializer.Serialize(x)));
            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
    }

    public ProcessIdentity? Lookup(int id)
    {
        lock (_lock) return id >= 0 && id < _byId.Count ? _byId[id] : null;
    }

    public void Dispose() => _writer?.Dispose();
}
