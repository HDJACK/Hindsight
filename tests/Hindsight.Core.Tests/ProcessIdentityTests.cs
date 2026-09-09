using System.Text.Json;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Tests;

public class ProcessIdentityTests
{
    [Fact]
    public void OldJsonLine_DeserializesWithEmptyNewFields()
    {
        var id = JsonSerializer.Deserialize<ProcessIdentity>("""{"Pid":4,"StartTime":7,"Name":"svchost.exe","Path":"C:\\x","CommandLine":"","ParentPid":1}""")!;
        Assert.Equal("", id.Services); Assert.Equal("", id.Description); Assert.Equal("", id.Company);
        Assert.Equal("svchost.exe", id.DisplayName);
    }

    [Fact]
    public void DisplayName_IncludesServices_AndJsonIgnoresIt()
    {
        var id = new ProcessIdentity(4, 7, "svchost.exe", "", "", 1, "BITS, wuauserv", "Host Process", "Microsoft");
        Assert.Equal("svchost.exe (BITS, wuauserv)", id.DisplayName);
        string json = JsonSerializer.Serialize(id);
        Assert.DoesNotContain("DisplayName", json);
        Assert.Equal(id, JsonSerializer.Deserialize<ProcessIdentity>(json));
    }

    [Fact]
    public void Table_SetServices_UpdatesAndReportsChange()
    {
        var t = new ProcessTable();
        t.Upsert(new ProcessIdentity(4, 7, "svchost.exe", "", "", 1));
        Assert.NotNull(t.SetServices(4, "wuauserv"));
        Assert.Null(t.SetServices(4, "wuauserv"));
        Assert.Equal("wuauserv", t.GetByPid(4)!.Services);
        Assert.Null(t.SetServices(99, "x"));
        t.Upsert(new ProcessIdentity(4, 7, "svchost.exe", @"C:\s.exe", "", 1));   // re-upsert without services keeps them
        Assert.Equal("wuauserv", t.GetByPid(4)!.Services);
        Assert.Equal(@"C:\s.exe", t.GetByPid(4)!.Path);
    }

    [Fact]
    public void ContextEntry_MinuteOf()
    {
        Assert.Equal(1200, ContextEntry.MinuteOf(1259));
        Assert.Equal(1260, ContextEntry.MinuteOf(1260));
    }

    [Fact]
    public void MarkerKinds_AppendedInOrder()
    {
        Assert.Equal(7, (int)MarkerKind.WindowsUpdate);
        Assert.Equal(13, (int)MarkerKind.DisplayOn);
    }
}
