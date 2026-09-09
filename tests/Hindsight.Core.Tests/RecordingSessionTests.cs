using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

public class RecordingSessionTests
{
    private sealed class NoIds : IIdentitySource { public ProcessIdentity? Lookup(int id) => null; public ProcessIdentity? ByPid(int pid) => null; }

    private static RecordingSession.Environment Env(
        string folder,
        bool running = true,
        IReadOnlyList<Tick>? buffer = null,
        IReadOnlyList<Marker>? markers = null,
        Func<long>? now = null,
        Func<long, long, IReadOnlyList<ContextEntry>>? contextInRange = null) =>
        new(
            Identities: () => new NoIds(),
            ReadBuffer: () => buffer ?? Array.Empty<Tick>(),
            MarkersInRange: (a, b) => (markers ?? Array.Empty<Marker>()).Where(m => m.UnixTime >= a && m.UnixTime <= b).ToList(),
            IntervalSeconds: () => 1,
            CoreCount: 1,
            Folder: () => folder,
            PipelineRunning: () => running,
            UtcNowUnix: now,
            ContextInRange: contextInRange);

    [Fact]
    public void Start_WhileRecording_Throws()
    {
        using var d = new TempDir();
        var session = new RecordingSession(Env(d.Path));
        session.Start("a", "");
        Assert.Throws<InvalidOperationException>(() => session.Start("b", ""));
    }

    [Fact]
    public void Start_WhenPipelineStopped_Throws()
    {
        using var d = new TempDir();
        var session = new RecordingSession(Env(d.Path, running: false));
        Assert.Throws<InvalidOperationException>(() => session.Start("a", ""));
    }

    [Fact]
    public void StopWithZeroTicks_WritesReadableFile()
    {
        using var d = new TempDir();
        var session = new RecordingSession(Env(d.Path));
        session.Start("empty", "");
        string path = session.Stop();
        var r = RecordingReader.Open(path);
        Assert.Empty(r.Ticks);
    }

    [Fact]
    public void Append_ThenStop_WritesTicksAndOnlyMarkersInsideWindow()
    {
        using var d = new TempDir();
        long t = 1000;
        var markers = new[]
        {
            new Marker(999, MarkerKind.Manual, "before"),
            new Marker(1005, MarkerKind.Manual, "in"),
            new Marker(1011, MarkerKind.Manual, "after"),
        };
        var session = new RecordingSession(Env(d.Path, markers: markers, now: () => t));
        session.Start("run", "");
        session.Append(new Tick { UnixTime = 1001, IntervalSeconds = 1 });
        session.Append(new Tick { UnixTime = 1005, IntervalSeconds = 1 });
        session.Append(new Tick { UnixTime = 1009, IntervalSeconds = 1 });
        t = 1010;
        string path = session.Stop();
        var r = RecordingReader.Open(path);
        Assert.Equal(3, r.Ticks.Count);
        Assert.Single(r.Markers);
        Assert.Equal("in", r.Markers[0].Text);
    }

    [Fact]
    public void Stop_WithContextInRange_WritesContext()
    {
        using var d = new TempDir();
        long t = 1000;
        var session = new RecordingSession(Env(d.Path, now: () => t,
            contextInRange: (a, b) => new[] { new ContextEntry(ContextEntry.MinuteOf(a), 1, 0, ContextKind.Dns, "x", 1) }));
        session.Start("run", "");
        session.Append(new Tick { UnixTime = 1000, IntervalSeconds = 1 });
        t = 1010;
        string path = session.Stop();
        var r = RecordingReader.Open(path);
        Assert.Single(r.Context);
    }

    [Fact]
    public void SaveBuffer_FiltersMarkersToBufferRange()
    {
        using var d = new TempDir();
        var ticks = new List<Tick>
        {
            new() { UnixTime = 100, IntervalSeconds = 1 },
            new() { UnixTime = 101, IntervalSeconds = 1 },
            new() { UnixTime = 102, IntervalSeconds = 1 },
        };
        var markers = new[]
        {
            new Marker(99, MarkerKind.Manual, "before"),
            new Marker(101, MarkerKind.Manual, "in"),
            new Marker(103, MarkerKind.Manual, "after"),
        };
        var session = new RecordingSession(Env(d.Path, buffer: ticks, markers: markers));
        string path = session.SaveBuffer("snap", "");
        var r = RecordingReader.Open(path);
        Assert.Equal(3, r.Ticks.Count);
        Assert.Single(r.Markers);
        Assert.Equal("in", r.Markers[0].Text);
    }

    [Fact]
    public void Stop_WhenFinishFails_ClearsStateAndRaisesChanged()
    {
        using var d = new TempDir();
        string filePath = Path.Combine(d.Path, "not-a-folder");
        File.WriteAllText(filePath, "x");
        var session = new RecordingSession(Env(filePath));
        session.Start("a", "");
        bool changed = false;
        session.Changed += () => changed = true;
        Assert.ThrowsAny<Exception>(() => session.Stop());
        Assert.False(session.IsRecording);
        Assert.True(changed);
    }

    [Fact]
    public void StopIfRecording_ReturnsNullWhenIdle_AndPathWhenRecording()
    {
        using var d = new TempDir();
        var session = new RecordingSession(Env(d.Path));
        Assert.Null(session.StopIfRecording());
        session.Start("a", "");
        string? path = session.StopIfRecording();
        Assert.NotNull(path);
        Assert.False(session.IsRecording);
    }
}
