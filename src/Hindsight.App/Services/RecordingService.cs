using System.IO;
using Hindsight.Core.Model;
using Hindsight.Core.Recordings;

namespace Hindsight.App.Services;

/// <summary>Thin WPF-side wrapper around <see cref="RecordingSession"/>, wired to the live <see cref="AppServices"/> pipeline.</summary>
public sealed class RecordingService
{
    private readonly RecordingSession _session;

    public bool IsRecording => _session.IsRecording;
    public string? CurrentName => _session.CurrentName;
    public string? CurrentNotes => _session.CurrentNotes;
    public DateTimeOffset? StartedAt => _session.StartedAt;
    public int TickCount => _session.TickCount;
    public string Folder => _session.Folder;
    public event Action? Changed { add => _session.Changed += value; remove => _session.Changed -= value; }

    public RecordingService(AppServices services)
    {
        _session = new RecordingSession(new RecordingSession.Environment(
            Identities: () => services.LiveIdentities,
            ReadBuffer: () => services.Ring.ReadAll(),
            MarkersInRange: (a, b) => services.Markers.InRange(a, b),
            IntervalSeconds: () => services.Current.SampleIntervalSeconds,
            CoreCount: System.Environment.ProcessorCount,
            Folder: () => Path.Combine(services.Current.DataFolder, "Recordings"),
            PipelineRunning: () => services.IsRunning,
            ContextInRange: (a, b) =>
            {
                // Flush first: the newest entries live in the accumulator until the next tick is written.
                services.Recorder.FlushContext();
                return services.Context.InRange(a, b);
            }));
        services.TickWritten += Append;
    }

    private void Append(Tick tick) => _session.Append(tick);

    public void Start(string name, string notes) => _session.Start(name, notes);
    public string Stop() => _session.Stop();
    public string SaveBuffer(string name, string notes) => _session.SaveBuffer(name, notes);
    public string? StopIfRecording() => _session.StopIfRecording();
}
