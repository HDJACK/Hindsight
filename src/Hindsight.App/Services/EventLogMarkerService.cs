using System.Diagnostics.Eventing.Reader;
using Hindsight.Core.Context;
using Hindsight.Core.Model;

namespace Hindsight.App.Services;

/// <summary>Subscribes to the Windows Update, Defender and Task Scheduler operational logs and turns records into markers.</summary>
public sealed class EventLogMarkerService : IDisposable
{
    private readonly Action<Marker> _addMarker;
    private readonly List<EventLogWatcher> _watchers = new();
    private readonly List<string> _notes = new();
    private readonly object _rateLock = new();
    private long _lastScheduledTaskUnix = long.MinValue;
    private bool _disposed;

    /// <summary>A busy machine fires Task Scheduler "task started" events several times a second; at most one
    /// scheduled-task marker is kept per minute so the timeline (and markers.jsonl) stay readable.</summary>
    private const int ScheduledTaskMinIntervalSeconds = 60;

    public IReadOnlyList<string> Notes => _notes;

    public EventLogMarkerService(Action<Marker> addMarker)
    {
        _addMarker = addMarker;
        Subscribe(EventLogMarkers.WindowsUpdateLog, EventLogMarkers.WindowsUpdateQuery, "Windows Update log");
        Subscribe(EventLogMarkers.DefenderLog, EventLogMarkers.DefenderQuery, "Defender log");
        Subscribe(EventLogMarkers.TaskSchedulerLog, EventLogMarkers.TaskSchedulerQuery, "Task Scheduler log");
    }

    private void Subscribe(string log, string xpath, string shortName)
    {
        EventLogWatcher? watcher = null;
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath);
            watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += (_, e) => OnRecord(log, e);
            watcher.Enabled = true;
            _watchers.Add(watcher);
        }
        catch (EventLogNotFoundException ex) { Fail(watcher, shortName, ex.Message); }
        catch (UnauthorizedAccessException ex) { Fail(watcher, shortName, ex.Message); }
        catch (EventLogException ex) { Fail(watcher, shortName, ex.Message); }
    }

    private void Fail(EventLogWatcher? watcher, string shortName, string message)
    {
        watcher?.Dispose();
        _notes.Add($"Event log markers: {shortName} unavailable ({message})");
    }

    // Raised on a thread-pool thread; MarkerStore.Add is thread-safe.
    private void OnRecord(string log, EventRecordWrittenEventArgs e)
    {
        try
        {
            if (e.EventRecord is null) return;
            using var record = e.EventRecord;
            var props = record.Properties.Select(p => p.Value?.ToString() ?? "").ToList();
            var marker = EventLogMarkers.Map(log, record.Id, props, Unix(record.TimeCreated));
            if (marker is null) return;
            if (marker.Kind == MarkerKind.ScheduledTask && !AllowScheduledTask(marker.UnixTime)) return;
            _addMarker(marker);
        }
        catch (EventLogException) { /* the log went away or the record could not be read */ }
        catch (UnauthorizedAccessException) { /* access revoked while running */ }
        // The watcher raises this on a thread-pool thread, so anything escaping here would tear the process down:
        // log and drop instead (e.g. the marker store was disposed by a pipeline restart mid-callback).
        catch (Exception ex) { Log.Error("EventLog", ex); }
    }

    /// <summary>True for the first scheduled-task marker in a 60 s window; the rest are dropped.
    /// Called from several thread-pool threads at once, so the last-kept time is guarded.</summary>
    private bool AllowScheduledTask(long unixTime)
    {
        lock (_rateLock)
        {
            if (_lastScheduledTaskUnix != long.MinValue && unixTime - _lastScheduledTaskUnix < ScheduledTaskMinIntervalSeconds)
                return false;
            _lastScheduledTaskUnix = unixTime;
            return true;
        }
    }

    private static long Unix(DateTime? time) =>
        time is null
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            : new DateTimeOffset(time.Value.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeSeconds();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _watchers)
        {
            try { w.Enabled = false; } catch (EventLogException) { /* already gone */ }
            w.Dispose();
        }
        _watchers.Clear();
    }
}
