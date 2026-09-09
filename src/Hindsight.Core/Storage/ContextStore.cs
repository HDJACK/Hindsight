using System.Text.Json;
using Hindsight.Core.Context;
using Hindsight.Core.Model;

namespace Hindsight.Core.Storage;

/// <summary>Per-minute context entries, held in memory and appended to a JSON Lines file.</summary>
public sealed class ContextStore : IContextSource, IDisposable
{
    private readonly List<ContextEntry> _all = new();
    private StreamWriter? _writer;
    private readonly string? _path;
    private readonly object _lock = new();
    private bool _disposed;

    public ContextStore(string? path, long keepFromUnix)
    {
        _path = path;
        if (path is null) return;

        long keepFromMinute = ContextEntry.MinuteOf(keepFromUnix);
        bool dropped = false;
        if (File.Exists(path))
        {
            foreach (var line in JsonLinesFile.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var e = JsonSerializer.Deserialize<ContextEntry>(line);
                    if (e is null) continue;
                    if (e.Minute < keepFromMinute) { dropped = true; continue; }
                    MergeLocked(e);
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }

        // The append writer is opened lazily (on first Append) rather than here, so that a
        // store which only loads/trims on construction never holds an open file handle that
        // would block a plain read of the file by another process.
        if (dropped) RewriteLocked();
    }

    public int Count { get { lock (_lock) return _all.Count; } }

    public bool HasContext => true;

    public void Append(IEnumerable<ContextEntry> entries)
    {
        lock (_lock)
        {
            if (_disposed) return;
            EnsureWriterLocked();
            foreach (var e in entries)
            {
                MergeLocked(e);
                // The file remains append-only (a second line for the same key is possible), but the
                // loader above merges duplicates the same way, so memory always holds one row per key.
                _writer?.WriteLine(JsonSerializer.Serialize(e));
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="e"/> to the in-memory list, merging it into an existing entry with the
    /// same (Minute, Pid, Kind, Key) by summing Value and keeping the larger StartTime. Caller must
    /// hold _lock (or be the single-threaded constructor).
    /// </summary>
    private void MergeLocked(ContextEntry e)
    {
        for (int i = 0; i < _all.Count; i++)
        {
            var existing = _all[i];
            if (existing.Minute == e.Minute && existing.Pid == e.Pid && existing.Kind == e.Kind && existing.Key == e.Key)
            {
                _all[i] = existing with
                {
                    Value = existing.Value + e.Value,
                    StartTime = Math.Max(existing.StartTime, e.StartTime),
                };
                return;
            }
        }
        _all.Add(e);
    }

    public IReadOnlyList<ContextEntry> ForMinute(long minute, int pid)
    {
        lock (_lock)
            return _all
                .Where(e => e.Minute == minute && e.Pid == pid)
                .OrderBy(e => e.Kind)
                .ThenByDescending(e => e.Value)
                .ToArray();
    }

    /// <summary>Entries whose Minute lies in [MinuteOf(fromUnix), toUnix].</summary>
    public IReadOnlyList<ContextEntry> InRange(long fromUnix, long toUnix)
    {
        long fromMinute = ContextEntry.MinuteOf(fromUnix);
        lock (_lock)
            return _all.Where(e => e.Minute >= fromMinute && e.Minute <= toUnix).ToArray();
    }

    public void Trim(long olderThanUnix)
    {
        long keepFromMinute = ContextEntry.MinuteOf(olderThanUnix);
        lock (_lock)
        {
            if (_disposed) return;
            int removed = _all.RemoveAll(e => e.Minute < keepFromMinute);
            if (removed > 0) RewriteLocked();
        }
    }

    /// <summary>Rewrites the backing file from the in-memory list. Caller must hold _lock.</summary>
    private void RewriteLocked()
    {
        if (_path is null) return;
        bool hadWriter = _writer is not null;
        _writer?.Dispose();
        _writer = null;
        File.WriteAllLines(_path, _all.Select(e => JsonSerializer.Serialize(e)));
        if (hadWriter) EnsureWriterLocked();
    }

    /// <summary>Opens the append writer if it isn't already open. Caller must hold _lock.</summary>
    private void EnsureWriterLocked()
    {
        if (_writer is not null || _path is null) return;
        _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer?.Dispose();
        }
    }
}
