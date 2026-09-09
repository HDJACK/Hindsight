using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Tests;

public class ProcessTableTests
{
    [Fact]
    public void Get_RequiresMatchingStartTime()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(7, 100, "a.exe", "", "", 1));
        Assert.NotNull(t.Get(7, 100));
        Assert.Null(t.Get(7, 999));
        Assert.Equal("a.exe", t.GetByPid(7)!.Name);
    }

    [Fact]
    public void Upsert_PidReuse_ReplacesIdentity()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(7, 100, "old.exe", "", "", 1));
        t.Upsert(new ProcessIdentity(7, 200, "new.exe", "", "", 1));
        Assert.Equal("new.exe", t.GetByPid(7)!.Name);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Upsert_SameKey_KeepsRicherCommandLine()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(7, 100, "a.exe", @"C:\a.exe", "a.exe --flag", 1));
        t.Upsert(new ProcessIdentity(7, 100, "a.exe", "", "", 1));
        Assert.Equal("a.exe --flag", t.GetByPid(7)!.CommandLine);
        Assert.Equal(@"C:\a.exe", t.GetByPid(7)!.Path);
    }

    [Fact]
    public void Remove_OnlyWithMatchingStartTime()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(7, 200, "new.exe", "", "", 1));
        Assert.False(t.Remove(7, 100));
        Assert.True(t.Remove(7, 200));
        Assert.Equal(0, t.Count);
    }
}
