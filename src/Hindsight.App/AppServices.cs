using System.IO;
using Hindsight.App.Services;
using Hindsight.Core.Analysis;
using Hindsight.Core.Context;
using Hindsight.Core.Etw;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;
using Hindsight.Core.Sources;
using Hindsight.Core.Storage;
using Hindsight.Core.Win32;

namespace Hindsight.App;

/// <summary>Owns the recording pipeline and rebuilds it when recording settings change.</summary>
public sealed class AppServices : IDisposable
{
    private const int MaxIdentityLines = 50_000;

    public SettingsStore Settings { get; }
    public AppSettings Current => Settings.Current;
    public RingBuffer Ring { get; private set; } = null!;
    public IdentityStore Identities { get; private set; } = null!;
    public MarkerStore Markers { get; private set; } = null!;
    public ProcessTable Table { get; private set; } = null!;
    public Recorder Recorder { get; private set; } = null!;
    /// <summary>Durable per-second context entries (files, endpoints, services, events) for the buffer window.</summary>
    public ContextStore Context { get; private set; } = null!;
    /// <summary>Collects context entries from the sources between ticks; owned by the recorder while running.</summary>
    public ContextAccumulator ContextAccumulator { get; private set; } = null!;
    public bool IsRunning => _cts is not null;
    public RecordingService Recordings { get; }
    /// <summary>Identity lookups against the live buffer. One instance per pipeline build, so callers can use
    /// reference equality to tell "still the same source" from "the pipeline was rebuilt".</summary>
    public IIdentitySource LiveIdentities { get; private set; } = null!;

    /// <summary>PDH counters that could not be opened (used for the counter status text). Context problems are
    /// reported separately, through <see cref="ContextNoteSnapshot"/>.</summary>
    public IReadOnlyList<string> MissingCounters => _pdh?.MissingCounters ?? Array.Empty<string>();

    /// <summary>A snapshot of <see cref="ContextNotes"/>, taken under the list's lock.</summary>
    public IReadOnlyList<string> ContextNoteSnapshot
    {
        get { lock (ContextNotes) return ContextNotes.ToArray(); }
    }
    /// <summary>Non-fatal notes from context services (event log, ETW context), shown in the UI.</summary>
    public List<string> ContextNotes { get; } = new();

    public event Action<Tick>? TickWritten;
    public event Action? PipelineRestarted;
    public event Action<string>? RecordingAutoSaved;
    /// <summary>Raised after a note is added to <see cref="ContextNotes"/>, so the UI can refresh mid-session.</summary>
    public event Action? ContextNotesChanged;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private PdhSource? _pdh;
    private EtwSource? _etw;
    private long _lastTrimMinute = long.MinValue;

    /// <summary>Notes added from the ETW pipeline (file-paths-disabled, DNS-unavailable) that must be cleared on restart.</summary>
    private const string FilePathsDisabledNote = "File paths disabled after an ETW overload";
    private const string DnsUnavailableNote = "DNS names unavailable (provider could not be enabled)";

    public AppServices(SettingsStore settings)
    {
        Settings = settings;
        Recordings = new RecordingService(this);
    }

    public void Start()
    {
        Build(Current);
        Run();
    }

    /// <summary>Stops, rebuilds with the current settings (buffer, interval, folder) and starts again.</summary>
    public void RestartPipeline()
    {
        var saved = Recordings.StopIfRecording();
        if (saved is not null) RecordingAutoSaved?.Invoke(saved);
        Stop();
        Build(Current);
        Run();
        PipelineRestarted?.Invoke();
    }

    /// <summary>Adds a non-fatal context note (deduplicated, so a pipeline restart cannot repeat it).</summary>
    public void AddContextNote(string note)
    {
        bool added;
        lock (ContextNotes)
        {
            added = !ContextNotes.Contains(note);
            if (added) ContextNotes.Add(note);
        }
        if (added) ContextNotesChanged?.Invoke();
    }

    /// <summary>Removes the notes derived from the ETW pipeline (file paths, DNS) so a restart doesn't carry stale ones.</summary>
    private void ClearPipelineNotes()
    {
        lock (ContextNotes)
        {
            ContextNotes.Remove(FilePathsDisabledNote);
            ContextNotes.Remove(DnsUnavailableNote);
        }
    }

    public void AddMarker(MarkerKind kind, string text) =>
        AddMarkerAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), kind, text);

    /// <summary>Adds a marker at an explicit second (e.g. the second selected in the timeline).</summary>
    public void AddMarkerAt(long unixTime, MarkerKind kind, string text)
    {
        if (!IsRunning) return;
        Markers.Add(new Marker(unixTime, kind, text));
    }

    private void Build(AppSettings s)
    {
        ClearPipelineNotes();
        _lastTrimMinute = long.MinValue;
        Directory.CreateDirectory(s.DataFolder);
        string ringPath = Path.Combine(s.DataFolder, "ring.bin");
        string identitiesPath = Path.Combine(s.DataFolder, "identities.jsonl");
        string markersPath = Path.Combine(s.DataFolder, "markers.jsonl");
        string contextPath = Path.Combine(s.DataFolder, "context.jsonl");

        // The identity file grows without bound; past this size, reset history so ids stay small.
        if (File.Exists(identitiesPath) && File.ReadLines(identitiesPath).Count() > MaxIdentityLines)
        {
            File.Delete(identitiesPath);
            if (File.Exists(ringPath)) File.Delete(ringPath);
        }

        RingBuffer? ring = null; IdentityStore? ids = null; MarkerStore? markers = null; ContextStore? context = null;
        try
        {
            ring = RingBuffer.Open(ringPath, s.RingCapacity);
            ids = new IdentityStore(identitiesPath);
            // Tick.IdentityId indexes identities.jsonl; without it the history would name the wrong processes.
            if (ids.Count == 0 && ring.Count > 0)
            {
                ring.Dispose();
                File.Delete(ringPath);
                ring = RingBuffer.Open(ringPath, s.RingCapacity);
            }
            markers = new MarkerStore(markersPath);
            // Entries older than the buffer window are dropped while loading, so a stale file cannot grow the store.
            context = new ContextStore(contextPath, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.BufferMinutes * 60L);
        }
        catch
        {
            context?.Dispose(); markers?.Dispose(); ids?.Dispose(); ring?.Dispose();
            throw;
        }

        Ring = ring; Identities = ids; Markers = markers; Context = context;
        ContextAccumulator = new ContextAccumulator();
        Table = new ProcessTable();
        LiveIdentities = new LiveIdentitySource(Identities, Table);
        var reader = new ProcessInfoReader(() => Current.StoreCommandLines);
        var aggregator = new Aggregator(Table, Identities,
            new WeightedProcessRanker(() => Current),
            new ThresholdSpikeDetector(() => Current),
            Environment.ProcessorCount);
        _pdh = new PdhSource();
        var etw = new EtwSource(reader, ContextAccumulator, () => Current);
        // Raised on the recorder thread: MarkerStore.Add and the note list are thread-safe, and no WPF object is touched.
        etw.FilePathsDisabledByOverload += () =>
        {
            AddMarker(MarkerKind.Manual, "ETW overload, file paths disabled");
            AddContextNote(FilePathsDisabledNote);
        };
        _etw = etw;
        Recorder = new Recorder(Ring, aggregator,
            new ISampleSource[] { etw, new SnapshotSource(reader), _pdh, new ServiceMapSource(), new HostStateSource(() => Current) },
            new RecorderOptions(s.SampleIntervalSeconds), ContextAccumulator, Context,
            pid => Table.GetByPid(pid)?.StartTime ?? 0);
        Recorder.TickWritten += t =>
        {
            // Once a minute, drop context older than the buffer window. Not cheap: once the window is
            // full this rewrites the whole context file (Context.Trim -> RewriteLocked), on the recorder thread.
            long minute = t.UnixTime / 60;
            if (minute != _lastTrimMinute)
            {
                _lastTrimMinute = minute;
                long olderThan = t.UnixTime - Current.BufferMinutes * 60L;
                // Never trim past the start of a running recording: RecordingSession.Stop only collects
                // markers/context over [_startUnix, now], so trimming earlier would lose early data from a
                // recording longer than the buffer window.
                var started = Recordings.StartedAt;
                if (started is not null) olderThan = Math.Min(olderThan, started.Value.ToUnixTimeSeconds());
                Context.Trim(olderThan);
                // Automatic markers (event log, power, session) would otherwise grow markers.jsonl without
                // bound; the user's own markers survive the window so a saved recording keeps them.
                Markers.Trim(olderThan, MarkerKind.Manual);
            }
            TickWritten?.Invoke(t);
        };
    }

    private void Run()
    {
        Recorder.Start();
        if (_etw?.DnsUnavailable == true)
            AddContextNote(DnsUnavailableNote);
        _cts = new CancellationTokenSource();
        var recorder = Recorder; var token = _cts.Token;
        _loop = Task.Run(() => recorder.RunAsync(token));
    }

    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* cancellation */ }
        Recorder.Dispose();   // flushes pending context into the store first
        Context.Dispose();
        Markers.Dispose();
        Identities.Dispose();
        Ring.Dispose();
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        var saved = Recordings.StopIfRecording();
        if (saved is not null) RecordingAutoSaved?.Invoke(saved);
        _disposed = true;
        Stop();
    }
}
