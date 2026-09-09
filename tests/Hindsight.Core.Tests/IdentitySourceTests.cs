using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class IdentitySourceTests
{
    [Fact]
    public void LiveSource_DelegatesToStoreAndTable()
    {
        using var store = new IdentityStore(null);
        var table = new ProcessTable();
        var id = new ProcessIdentity(7, 1, "a.exe", "", "", 0);
        int idx = store.Intern(id);
        table.Upsert(id);
        var src = new LiveIdentitySource(store, table);
        Assert.Equal("a.exe", src.Lookup(idx)!.Name);
        Assert.Null(src.Lookup(99));
        Assert.Equal("a.exe", src.ByPid(7)!.Name);
        Assert.Null(src.ByPid(8));
    }

    [Fact]
    public void ParentChain_WorksWithIdentitySource()
    {
        using var store = new IdentityStore(null);
        var table = new ProcessTable();
        table.Upsert(new ProcessIdentity(1, 1, "explorer.exe", "", "", 0));
        table.Upsert(new ProcessIdentity(2, 1, "cmd.exe", "", "", 1));
        var leaf = new ProcessIdentity(3, 1, "x.exe", "", "", 2);
        Assert.Equal("explorer.exe > cmd.exe > x.exe", ParentChain.Describe(leaf, new LiveIdentitySource(store, table)));
    }

    [Fact]
    public void Tick_Copy_IsDeep()
    {
        var t = new Tick { UnixTime = 5, CpuTotalPercent = 1, Flags = TickFlags.Spike, IntervalSeconds = 2, GpuTotalPercent = 3.5f, ForegroundPid = 42, Samples = new[] { new ProcessSample { Pid = 1, IdentityId = 9 } } };
        var c = t.Copy();
        c.Samples[0].IdentityId = 0;
        Assert.Equal(9, t.Samples[0].IdentityId);
        Assert.Equal(5, c.UnixTime); Assert.Equal(TickFlags.Spike, c.Flags); Assert.Equal(2, c.IntervalSeconds);
        Assert.Equal(3.5f, c.GpuTotalPercent); Assert.Equal(42, c.ForegroundPid);
    }
}
