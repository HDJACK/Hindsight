using System.Text.Json;
using Hindsight.Core.Model;

namespace Hindsight.Core.Storage;

/// <summary>Markers kept in time order, appended to a JSON Lines file and trimmed along with the buffer.</summary>
public sealed class MarkerStore : IDisposable
{
    private readonly List<Marker> _all = new();
    private StreamWriter? _writer;
    private readonly string? _path;
    private readonly object _lock = new();
    private bool _disposed;

    public event Action<Marker>? Added;
    public event Action<Marker>? Removed;

    public MarkerStore(string? path)
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
                    var m = JsonSerializer.Deserialize<Marker>(line);
                    if (m is not null) _all.Add(m);
                }
                catch (JsonException)
                {
                    continue;
                }
            }
            _all.Sort((a, b) => a.UnixTime.CompareTo(b.UnixTime));
        }
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public IReadOnlyList<Marker> All { get { lock (_lock) return _all.ToArray(); } }

    public void Add(Marker m)
    {
        lock (_lock)
        {
            // Markers arrive from event-log and system-event callbacks on thread-pool threads, which can
            // outlive a pipeline teardown; after Dispose the writer is gone, so drop the marker instead.
            if (_disposed) return;
            int i = _all.FindIndex(x => x.UnixTime > m.UnixTime);
            if (i < 0) _all.Add(m); else _all.Insert(i, m);
            _writer?.WriteLine(JsonSerializer.Serialize(m));
        }
        Added?.Invoke(m);
    }

    /// <summary>Removes a marker (matched by time, kind and text) and rewrites the file. Returns false when not found.</summary>
    public bool Remove(Marker m)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            int i = _all.FindIndex(x => x.UnixTime == m.UnixTime && x.Kind == m.Kind && x.Text == m.Text);
            if (i < 0) return false;
            _all.RemoveAt(i);
            RewriteLocked();
        }
        Removed?.Invoke(m);
        return true;
    }

    public IReadOnlyList<Marker> InRange(long fromUnix, long toUnix)
    {
        lock (_lock) return _all.Where(m => m.UnixTime >= fromUnix && m.UnixTime <= toUnix).ToArray();
    }

    /// <summary>Drops markers older than <paramref name="olderThanUnix"/> and rewrites the file. Markers whose kind is
    /// <paramref name="keepKind"/> (the user's own, by default none) survive. Does not raise <see cref="Removed"/>:
    /// the trimmed markers lie outside the buffer window, so nothing on screen shows them.</summary>
    public void Trim(long olderThanUnix, MarkerKind? keepKind = null)
    {
        lock (_lock)
        {
            if (_disposed) return;
            int removed = _all.RemoveAll(m => m.UnixTime < olderThanUnix && m.Kind != keepKind);
            if (removed > 0) RewriteLocked();
        }
    }

    /// <summary>Rewrites the backing file from the in-memory list. Caller must hold _lock.</summary>
    private void RewriteLocked()
    {
        if (_path is null) return;
        _writer?.Dispose();
        File.WriteAllLines(_path, _all.Select(x => JsonSerializer.Serialize(x)));
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
