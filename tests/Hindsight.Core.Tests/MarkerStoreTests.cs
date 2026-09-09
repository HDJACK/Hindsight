using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class MarkerStoreTests
{
    [Fact]
    public void InRange_IsInclusiveAndSorted()
    {
        using var s = new MarkerStore(null);
        s.Add(new Marker(30, MarkerKind.Lock, "Lock"));
        s.Add(new Marker(10, MarkerKind.Manual, "one"));
        s.Add(new Marker(20, MarkerKind.Resume, "on"));
        var r = s.InRange(10, 20);
        Assert.Equal(new long[] { 10, 20 }, r.Select(m => m.UnixTime));
        Assert.Empty(s.InRange(31, 40));
    }

    [Fact]
    public void Add_RaisesEvent()
    {
        using var s = new MarkerStore(null);
        Marker? got = null;
        s.Added += m => got = m;
        s.Add(new Marker(1, MarkerKind.Manual, "x"));
        Assert.Equal("x", got!.Text);
    }

    [Fact]
    public void Reopen_PreservesMarkers()
    {
        using var f = new TempFile();
        using (var s = new MarkerStore(f.Path)) s.Add(new Marker(5, MarkerKind.AcDisconnected, "AC power off"));
        using var s2 = new MarkerStore(f.Path);
        Assert.Single(s2.All);
        Assert.Equal(MarkerKind.AcDisconnected, s2.All[0].Kind);
    }

    [Fact]
    public void CorruptLine_IsSkipped_OnReopen()
    {
        using var f = new TempFile();
        using (var s = new MarkerStore(f.Path)) s.Add(new Marker(5, MarkerKind.Manual, "eins"));
        File.AppendAllText(f.Path, "garbage\n");
        using (var s = new MarkerStore(f.Path)) s.Add(new Marker(6, MarkerKind.Manual, "two"));
        using var s2 = new MarkerStore(f.Path);
        Assert.Equal(2, s2.All.Count);
    }

    [Fact]
    public void Remove_DeletesMarker_AndPersistsAcrossReopen()
    {
        using var f = new TempFile();
        using (var s = new MarkerStore(f.Path))
        {
            s.Add(new Marker(10, MarkerKind.Manual, "keep"));
            s.Add(new Marker(20, MarkerKind.Manual, "drop"));
            Assert.True(s.Remove(new Marker(20, MarkerKind.Manual, "drop")));
            Assert.False(s.Remove(new Marker(99, MarkerKind.Manual, "none")));
            Assert.Single(s.All);
        }
        using var s2 = new MarkerStore(f.Path);
        Assert.Single(s2.All);
        Assert.Equal("keep", s2.All[0].Text);
    }

    [Fact]
    public void SecondStore_CanReadWhileFirstIsOpenForWriting()
    {
        using var f = new TempFile();
        using var writer = new MarkerStore(f.Path);
        writer.Add(new Marker(1, MarkerKind.Manual, "a"));
        using var reader = new MarkerStore(f.Path);
        Assert.Single(reader.All);
    }

    [Fact]
    public void Trim_DropsOldAutomaticMarkers_AndKeepsManualOnes()
    {
        using var f = new TempFile();
        using (var s = new MarkerStore(f.Path))
        {
            s.Add(new Marker(10, MarkerKind.ScheduledTask, "old task"));
            s.Add(new Marker(20, MarkerKind.Manual, "old manual"));
            s.Add(new Marker(200, MarkerKind.ScheduledTask, "recent task"));
            s.Trim(100, MarkerKind.Manual);
            Assert.Equal(new long[] { 20, 200 }, s.All.Select(m => m.UnixTime));
        }
        // The rewrite must reach the file, not just the in-memory list.
        using var s2 = new MarkerStore(f.Path);
        Assert.Equal(new long[] { 20, 200 }, s2.All.Select(m => m.UnixTime));
    }

    [Fact]
    public void Trim_WithoutKeepKind_DropsEverythingOlder()
    {
        using var s = new MarkerStore(null);
        s.Add(new Marker(10, MarkerKind.Manual, "old"));
        s.Add(new Marker(20, MarkerKind.Manual, "new"));
        s.Trim(20);
        Assert.Equal(new long[] { 20 }, s.All.Select(m => m.UnixTime));
    }

    [Fact]
    public void Trim_IsANoOp_WhenNothingIsOldEnough()
    {
        using var f = new TempFile();
        using var s = new MarkerStore(f.Path);
        s.Add(new Marker(200, MarkerKind.ScheduledTask, "recent"));
        s.Trim(100, MarkerKind.Manual);
        Assert.Single(s.All);
        s.Add(new Marker(300, MarkerKind.ScheduledTask, "later"));
        Assert.Equal(2, s.All.Count);
    }

    [Fact]
    public void AfterDispose_AddRemoveAndTrimAreDropped_NotThrowing()
    {
        using var f = new TempFile();
        var s = new MarkerStore(f.Path);
        s.Add(new Marker(10, MarkerKind.Manual, "before"));
        s.Dispose();
        s.Dispose();                                                      // idempotent
        s.Add(new Marker(20, MarkerKind.ScheduledTask, "after dispose"));  // a late event-log callback
        Assert.False(s.Remove(new Marker(10, MarkerKind.Manual, "before")));
        s.Trim(100, MarkerKind.Manual);
        using var s2 = new MarkerStore(f.Path);
        Assert.Single(s2.All);
        Assert.Equal("before", s2.All[0].Text);
    }
}
