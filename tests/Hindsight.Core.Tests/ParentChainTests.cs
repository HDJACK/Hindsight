using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Tests;

public class ParentChainTests
{
    [Fact]
    public void Describe_WalksUpToMaxDepth()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(1, 1, "explorer.exe", "", "", 0));
        t.Upsert(new ProcessIdentity(2, 1, "cmd.exe", "", "", 1));
        t.Upsert(new ProcessIdentity(3, 1, "build.exe", "", "", 2));
        t.Upsert(new ProcessIdentity(4, 1, "cl.exe", "", "", 3));
        Assert.Equal("cmd.exe > build.exe > cl.exe", ParentChain.Describe(t.GetByPid(4)!, t, 2));
        Assert.Equal("explorer.exe > cmd.exe > build.exe > cl.exe", ParentChain.Describe(t.GetByPid(4)!, t, 5));
    }

    [Fact]
    public void Describe_UnknownParent_ShowsPid()
    {
        var t = new ProcessTable();
        var id = new ProcessIdentity(9, 1, "x.exe", "", "", 4242);
        t.Upsert(id);
        Assert.Equal("PID 4242 > x.exe", ParentChain.Describe(id, t));
    }

    [Fact]
    public void Describe_NoParent_JustName()
    {
        var t = new ProcessTable();
        var id = new ProcessIdentity(9, 1, "x.exe", "", "", 0);
        Assert.Equal("x.exe", ParentChain.Describe(id, t));
    }
}
