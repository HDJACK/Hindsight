using System.IO.Compression;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

public class RecordingCatalogTests
{
    private sealed class NoIds : IIdentitySource { public ProcessIdentity? Lookup(int id) => null; public ProcessIdentity? ByPid(int pid) => null; }

    private static string Save(string folder, string name, long start)
    {
        var ticks = new List<Tick> { new() { UnixTime = start, IntervalSeconds = 1 } };
        return RecordingWriter.SaveRange(folder, name, "", ticks, Array.Empty<Marker>(), new NoIds(), 1, 1);
    }

    [Fact]
    public void List_NewestFirst_UnreadableLast()
    {
        using var d = new TempDir();
        Save(d.Path, "old", 1000); Save(d.Path, "new", 2000);
        File.WriteAllText(Path.Combine(d.Path, "junk.hsrec"), "nope");
        var cat = new RecordingCatalog(d.Path);
        var list = cat.List();
        Assert.Equal(3, list.Count);
        Assert.Equal("new", list[0].DisplayName);
        Assert.Equal("old", list[1].DisplayName);
        Assert.False(list[2].IsReadable); Assert.Equal("(unreadable)", list[2].DisplayName); Assert.NotNull(list[2].Error);
        Assert.True(list[0].SizeBytes > 0);
    }

    [Fact]
    public void List_CreatesMissingFolder()
    {
        using var d = new TempDir();
        var cat = new RecordingCatalog(Path.Combine(d.Path, "sub"));
        Assert.Empty(cat.List());
        Assert.True(Directory.Exists(cat.Folder));
    }

    [Fact]
    public void Rename_UpdatesMetaOnly_AndKeepsTicks()
    {
        using var d = new TempDir();
        string p = Save(d.Path, "before", 1000);
        new RecordingCatalog(d.Path).Rename(p, "after");
        Assert.True(File.Exists(p));
        Assert.Equal("after", RecordingCatalog.ReadMeta(p).Name);
        Assert.Single(RecordingReader.Open(p).Ticks);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        using var d = new TempDir();
        string p = Save(d.Path, "x", 1000);
        new RecordingCatalog(d.Path).Delete(p);
        Assert.False(File.Exists(p));
    }
}
