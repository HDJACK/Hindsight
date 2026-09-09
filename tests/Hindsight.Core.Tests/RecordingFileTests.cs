using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

public class RecordingFileTests
{
    private sealed class MapSource(Dictionary<int, ProcessIdentity> byId) : IIdentitySource
    {
        public ProcessIdentity? Lookup(int id) => byId.TryGetValue(id, out var v) ? v : null;
        public ProcessIdentity? ByPid(int pid) => byId.Values.LastOrDefault(v => v.Pid == pid);
    }

    private static (List<Tick> ticks, IIdentitySource ids) Sample()
    {
        var ids = new MapSource(new()
        {
            [10] = new ProcessIdentity(1, 1, "a.exe", @"C:\a.exe", "a --x", 0),
            [20] = new ProcessIdentity(2, 1, "b.exe", @"C:\b.exe", "", 1),
            [30] = new ProcessIdentity(3, 1, "c.exe", "", "", 2),
        });
        var ticks = new List<Tick>
        {
            new() { UnixTime = 1000, CpuTotalPercent = 50, IntervalSeconds = 1, Samples = new[] { new ProcessSample { Pid = 2, IdentityId = 20, CpuPercent = 5 }, new ProcessSample { Pid = 1, IdentityId = 10, CpuPercent = 1 } } },
            new() { UnixTime = 1001, Flags = TickFlags.Gap, IntervalSeconds = 1 },
            new() { UnixTime = 1002, CpuTotalPercent = 10, IntervalSeconds = 1, Samples = new[] { new ProcessSample { Pid = 2, IdentityId = 20 }, new ProcessSample { Pid = 9, IdentityId = 777 } } },
        };
        return (ticks, ids);
    }

    [Fact]
    public void SaveRange_ThenOpen_RoundTripsWithDenseIds()
    {
        using var d = new TempDir();
        var (ticks, ids) = Sample();
        var markers = new[] { new Marker(999, MarkerKind.Manual, "before"), new Marker(1001, MarkerKind.Lock, "in"), new Marker(1003, MarkerKind.Manual, "after") };
        string path = RecordingWriter.SaveRange(d.Path, "My run", "notes here", ticks, markers, ids, 1, 8);

        Assert.EndsWith("_My_run.hsrec", path);
        using (var zip = ZipFile.OpenRead(path))
            Assert.Equal(new[] { "context.jsonl", "identities.jsonl", "markers.jsonl", "meta.json", "ticks.bin" }, zip.Entries.Select(e => e.Name).OrderBy(n => n));

        var r = RecordingReader.Open(path);
        Assert.Equal(3, r.Meta.FormatVersion);
        Assert.Equal("My run", r.Meta.Name);
        Assert.Equal("notes here", r.Meta.Notes);
        Assert.Equal(1000, r.Meta.StartUnix); Assert.Equal(1002, r.Meta.EndUnix);
        Assert.Equal(3, r.Meta.TickCount); Assert.Equal(8, r.Meta.CoreCount); Assert.Equal(1, r.Meta.IntervalSeconds);
        Assert.Equal(3, r.Meta.DurationSeconds);
        Assert.Equal(3, r.Ticks.Count);
        Assert.True(r.Ticks[1].Has(TickFlags.Gap));
        // ids dense in first-seen order: 20 -> 0, 10 -> 1, 777 (unknown) -> 2 placeholder
        Assert.Equal(0, r.Ticks[0].Samples[0].IdentityId);
        Assert.Equal(1, r.Ticks[0].Samples[1].IdentityId);
        Assert.Equal(2, r.Ticks[2].Samples[1].IdentityId);
        Assert.Equal(3, r.Identities.Count);
        Assert.Equal("b.exe", r.Lookup(0)!.Name);
        Assert.Equal("a --x", r.Lookup(1)!.CommandLine);
        Assert.StartsWith("PID 9", r.Lookup(2)!.Name);
        Assert.Null(r.Lookup(3));
        Assert.Equal("b.exe", r.ByPid(2)!.Name);
        Assert.Single(r.Markers);
        Assert.Equal("in", r.Markers[0].Text);
        // the caller's ticks were not mutated
        Assert.Equal(20, ticks[0].Samples[0].IdentityId);
    }

    [Fact]
    public void Writer_AppendFinish_AndEmptyRecordingIsValid()
    {
        using var d = new TempDir();
        var (ticks, ids) = Sample();
        var w = new RecordingWriter(ids, 2, 4);
        foreach (var t in ticks) w.Append(t);
        Assert.Equal(3, w.TickCount);
        string path = w.Finish(d.Path, "x", "");
        Assert.Equal(3, RecordingReader.Open(path).Ticks.Count);

        var empty = new RecordingWriter(ids, 1, 4).Finish(d.Path, "empty", "");
        var r = RecordingReader.Open(empty);
        Assert.Empty(r.Ticks); Assert.Equal(0, r.Meta.DurationSeconds); Assert.Empty(r.Identities);
    }

    [Fact]
    public void SaveRange_CollidingNames_GetSuffix()
    {
        using var d = new TempDir();
        var (ticks, ids) = Sample();
        string a = RecordingWriter.SaveRange(d.Path, "same", "", ticks, Array.Empty<Marker>(), ids, 1, 1);
        string b = RecordingWriter.SaveRange(d.Path, "same", "", ticks, Array.Empty<Marker>(), ids, 1, 1);
        Assert.NotEqual(a, b);
        Assert.EndsWith("_same_2.hsrec", b);
    }

    [Theory]
    [InlineData("My run", "My_run")] [InlineData("  a/b:c*?\"<>|  ", "a_b_c______")] [InlineData("", "recording")]
    public void SanitizeName(string input, string expected) => Assert.Equal(expected, RecordingWriter.SanitizeName(input));

    [Fact]
    public void Open_TruncatedTicks_ThrowsInvalidData()
    {
        using var d = new TempDir();
        var (ticks, ids) = Sample();
        string path = RecordingWriter.SaveRange(d.Path, "trunc", "", ticks, Array.Empty<Marker>(), ids, 1, 1);

        byte[] full;
        using (var zip = ZipFile.OpenRead(path))
        using (var s = zip.GetEntry("ticks.bin")!.Open())
        using (var ms = new MemoryStream()) { s.CopyTo(ms); full = ms.ToArray(); }
        byte[] half = full[..(full.Length / 2)];

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            zip.GetEntry("ticks.bin")!.Delete();
            using var s = zip.CreateEntry("ticks.bin").Open();
            s.Write(half, 0, half.Length);
        }

        Assert.Throws<InvalidDataException>(() => RecordingReader.Open(path));
    }

    [Fact]
    public void Open_FormatVersion1_ReadsLegacyTicks()
    {
        using var d = new TempDir();
        string path = Path.Combine(d.Path, "legacy.hsrec");

        var buf = new byte[TickSerializer.LegacyRecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0), 5);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open(), new UTF8Encoding(false)))
                w.Write("""{"FormatVersion":1,"Name":"legacy","Notes":"","StartUnix":5,"EndUnix":5,"IntervalSeconds":1,"CoreCount":1,"MachineName":"m","TickCount":1,"AppVersion":"1.0"}""");
            using (var s = zip.CreateEntry("ticks.bin").Open())
                s.Write(buf, 0, buf.Length);
            zip.CreateEntry("identities.jsonl");
            zip.CreateEntry("markers.jsonl");
        }

        var r = RecordingReader.Open(path);
        Assert.Equal(1, r.Meta.FormatVersion);
        Assert.Single(r.Ticks);
        Assert.Equal(5, r.Ticks[0].UnixTime);
    }

    [Fact]
    public void SaveRange_WithContext_RoundTrips_AndFiltersByRange()
    {
        using var dir = new TempDir();
        var ticks = new List<Tick> { new() { UnixTime = 1230, IntervalSeconds = 1 }, new() { UnixTime = 1231, IntervalSeconds = 1 } };
        var ctx = new[] { new ContextEntry(1200, 5, 0, ContextKind.File, "in", 1), new ContextEntry(1140, 5, 0, ContextKind.File, "before", 1), new ContextEntry(1260, 5, 0, ContextKind.File, "after", 1) };
        string path = RecordingWriter.SaveRange(dir.Path, "c", "", ticks, Array.Empty<Marker>(), new Recording(new RecordingMeta(3, "x", "", 0, 0, 1, 1, "m", 0, ""), Array.Empty<Tick>(), Array.Empty<ProcessIdentity>(), Array.Empty<Marker>()), 1, 1, ctx);
        var r = RecordingReader.Open(path);
        Assert.True(r.HasContext);
        Assert.Single(r.Context); Assert.Equal("in", r.Context[0].Key);
        Assert.Single(r.ForMinute(1200, 5)); Assert.Empty(r.ForMinute(1200, 6));
    }

    [Fact]
    public void Open_FormatVersion2_HasNoContext()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "v2.hsrec");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open()))
                w.Write("""{"FormatVersion":2,"Name":"v2","Notes":"","StartUnix":5,"EndUnix":5,"IntervalSeconds":1,"CoreCount":1,"MachineName":"m","TickCount":1,"AppVersion":"1.0"}""");
            using (var s = zip.CreateEntry("ticks.bin").Open()) { var buf = new byte[TickSerializer.RecordSize]; TickSerializer.Write(new Tick { UnixTime = 5 }, buf); s.Write(buf); }
            zip.CreateEntry("identities.jsonl"); zip.CreateEntry("markers.jsonl");
        }
        var r = RecordingReader.Open(path);
        Assert.Equal(2, r.Meta.FormatVersion); Assert.False(r.HasContext); Assert.Empty(r.Context);
    }

    [Fact]
    public void Open_CorruptFile_ThrowsInvalidData()
    {
        using var d = new TempDir();
        string p = Path.Combine(d.Path, "bad.hsrec");
        File.WriteAllText(p, "not a zip");
        Assert.Throws<InvalidDataException>(() => RecordingReader.Open(p));
        string p2 = Path.Combine(d.Path, "nometa.hsrec");
        using (var zip = ZipFile.Open(p2, ZipArchiveMode.Create)) zip.CreateEntry("ticks.bin");
        Assert.Throws<InvalidDataException>(() => RecordingReader.Open(p2));
    }
}
