using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Recordings;

/// <summary>UI-free recording state machine: start/stop a named recording fed by Append, or snapshot a buffer.</summary>
public sealed class RecordingSession
{
    public sealed record Environment(
        Func<IIdentitySource> Identities,
        Func<IReadOnlyList<Tick>> ReadBuffer,
        Func<long, long, IReadOnlyList<Marker>> MarkersInRange,
        Func<int> IntervalSeconds,
        int CoreCount,
        Func<string> Folder,
        Func<bool> PipelineRunning,
        Func<long>? UtcNowUnix = null,
        Func<long, long, IReadOnlyList<ContextEntry>>? ContextInRange = null);

    private readonly Environment _env;
    private readonly object _lock = new();
    private RecordingWriter? _writer;
    private long _startUnix;

    public bool IsRecording { get { lock (_lock) return _writer is not null; } }
    public string? CurrentName { get; private set; }
    public string? CurrentNotes { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public int TickCount { get { lock (_lock) return _writer?.TickCount ?? 0; } }
    public string Folder => _env.Folder();
    public event Action? Changed;

    public RecordingSession(Environment env) => _env = env;

    private long Now() => _env.UtcNowUnix?.Invoke() ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Feed a tick; safe from any thread.</summary>
    public void Append(Tick tick) { lock (_lock) _writer?.Append(tick); }

    public void Start(string name, string notes)
    {
        if (!_env.PipelineRunning()) throw new InvalidOperationException("The recording pipeline is stopped.");
        lock (_lock)
        {
            if (_writer is not null) throw new InvalidOperationException("A recording is already running.");
            _writer = new RecordingWriter(_env.Identities(), _env.IntervalSeconds(), _env.CoreCount);
            CurrentName = string.IsNullOrWhiteSpace(name) ? "Recording" : name.Trim();
            CurrentNotes = notes;
            _startUnix = Now();
            StartedAt = DateTimeOffset.FromUnixTimeSeconds(_startUnix).ToLocalTime();
        }
        Changed?.Invoke();
    }

    /// <summary>Writes the file first; state is cleared even if writing fails, so the UI never sticks in "recording".</summary>
    public string Stop()
    {
        RecordingWriter writer;
        lock (_lock) { writer = _writer ?? throw new InvalidOperationException("No recording is running."); }
        try
        {
            if (_env.PipelineRunning())
            {
                writer.AddMarkers(_env.MarkersInRange(_startUnix, Now()));
                if (_env.ContextInRange is not null) writer.AddContext(_env.ContextInRange(_startUnix, Now()));
            }
            return writer.Finish(_env.Folder(), CurrentName ?? "Recording", CurrentNotes ?? "");
        }
        finally
        {
            lock (_lock) { _writer = null; CurrentName = null; CurrentNotes = null; StartedAt = null; }
            Changed?.Invoke();
        }
    }

    public string SaveBuffer(string name, string notes)
    {
        if (!_env.PipelineRunning()) throw new InvalidOperationException("The recording pipeline is stopped.");
        var ticks = _env.ReadBuffer();
        var markers = ticks.Count == 0 ? Array.Empty<Marker>() : _env.MarkersInRange(ticks[0].UnixTime, ticks[^1].UnixTime);
        var context = ticks.Count == 0 || _env.ContextInRange is null
            ? Array.Empty<ContextEntry>()
            : _env.ContextInRange(ticks[0].UnixTime, ticks[^1].UnixTime);
        string path = RecordingWriter.SaveRange(_env.Folder(), string.IsNullOrWhiteSpace(name) ? "Buffer snapshot" : name.Trim(), notes,
            ticks, markers, _env.Identities(), _env.IntervalSeconds(), _env.CoreCount, context);
        Changed?.Invoke();
        return path;
    }

    /// <summary>Best-effort save of a running recording (app exit, session end). Returns the path or null.</summary>
    public string? StopIfRecording()
    {
        if (!IsRecording) return null;
        try { return Stop(); } catch { return null; }
    }
}
