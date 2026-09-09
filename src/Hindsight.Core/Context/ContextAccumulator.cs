using System.Runtime.InteropServices;
using Hindsight.Core.Model;

namespace Hindsight.Core.Context;

/// <summary>Accumulates per-(pid, kind) context observations between flushes, capping cardinality per bucket.</summary>
public sealed class ContextAccumulator
{
    public const int TopN = 10, MaxKeysPerBucket = 200, MaxKeyLength = 260;
    public const string OtherKey = "(other)";

    private Dictionary<(int Pid, ContextKind Kind), Dictionary<string, long>> _buckets = new();
    private readonly object _lock = new();

    public void Add(int pid, ContextKind kind, string key, long delta)
    {
        if (pid <= 0 || string.IsNullOrEmpty(key) || delta <= 0) return;
        if (key.Length > MaxKeyLength) key = key[..MaxKeyLength];

        lock (_lock)
        {
            // One hash lookup per dictionary: this runs for every ETW file, endpoint and DNS event.
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(_buckets, (pid, kind), out _);
            bucket ??= new Dictionary<string, long>();

            ref long slot = ref CollectionsMarshal.GetValueRefOrAddDefault(bucket, key, out bool existed);
            if (existed) { slot += delta; return; }

            // The key was just added. Past the cardinality cap it must not stay: take it back out
            // and fold the delta into (other) instead.
            if (bucket.Count > MaxKeysPerBucket)
            {
                bucket.Remove(key);
                ref long other = ref CollectionsMarshal.GetValueRefOrAddDefault(bucket, OtherKey, out _);
                other += delta;
                return;
            }
            slot = delta;
        }
    }

    public bool IsEmpty { get { lock (_lock) return _buckets.Count == 0; } }

    /// <summary>Top N per (pid, kind) by Value desc then Key; clears everything. startTimeOf resolves the pid's StartTime (0 when unknown).</summary>
    public IReadOnlyList<ContextEntry> Flush(long minute, Func<int, long> startTimeOf)
    {
        Dictionary<(int Pid, ContextKind Kind), Dictionary<string, long>> snapshot;
        lock (_lock)
        {
            if (_buckets.Count == 0) return Array.Empty<ContextEntry>();
            snapshot = _buckets;
            _buckets = new Dictionary<(int Pid, ContextKind Kind), Dictionary<string, long>>();
        }

        var result = new List<ContextEntry>();
        foreach (var ((pid, kind), bucket) in snapshot)
        {
            long startTime = startTimeOf(pid);
            var top = bucket
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(TopN);
            foreach (var kv in top)
                result.Add(new ContextEntry(minute, pid, startTime, kind, kv.Key, kv.Value));
        }

        return result
            .OrderBy(e => e.Kind)
            .ThenByDescending(e => e.Value)
            .ToList();
    }
}
