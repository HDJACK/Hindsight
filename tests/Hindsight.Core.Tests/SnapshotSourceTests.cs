using System.Diagnostics;
using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class SnapshotSourceTests
{
    [Fact]
    public void Read_Twice_ReportsOwnProcessWithIdentityAndDelta()
    {
        using var src = new SnapshotSource();
        src.Start();
        int me = Environment.ProcessId;

        var first = src.Read();
        var started = first.Started.SingleOrDefault(i => i.Pid == me);
        Assert.NotNull(started);
        Assert.False(string.IsNullOrEmpty(started!.Path));
        Assert.False(string.IsNullOrEmpty(started.CommandLine));
        Assert.True(started.StartTime > 0);
        Assert.True(first.Totals.ProcessCount > 10);
        Assert.True(first.Totals.RamAvailableBytes > 0);
        Assert.True(first.Totals.RamTotalBytes >= first.Totals.RamAvailableBytes);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 150) { /* burn CPU */ }

        var second = src.Read();
        var delta = second.Deltas.Single(d => d.Pid == me);
        Assert.Equal(started.StartTime, delta.StartTime);
        Assert.True(delta.CpuSeconds > 0.05, $"CpuSeconds was {delta.CpuSeconds}");
        Assert.True(delta.WorkingSet > 0);
        Assert.Empty(second.Started.Where(i => i.Pid == me));
        Assert.InRange(second.Totals.CpuPercent, 0, 100);
        Assert.True(delta.HandleCount > 10, $"HandleCount was {delta.HandleCount}");
        Assert.True(delta.ThreadCount > 1, $"ThreadCount was {delta.ThreadCount}");
        Assert.True(delta.PrivateBytes > 1_000_000, $"PrivateBytes was {delta.PrivateBytes}");
        Assert.True(delta.HardFaults >= 0, $"HardFaults was {delta.HardFaults}");
    }

    [Fact]
    public void Read_ReportsVanishedProcessAsExited()
    {
        using var src = new SnapshotSource();
        src.Start();

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        Assert.NotNull(child);

        var first = src.Read();
        var started = first.Started.Single(i => i.Pid == child!.Id);

        child!.WaitForExit();

        var second = src.Read();
        var exit = second.Exited.Single(e => e.Identity.Pid == child.Id);
        Assert.Equal(started.StartTime, exit.Identity.StartTime);
    }

    [Fact]
    public void Read_BeforeStart_Throws()
    {
        using var src = new SnapshotSource();
        Assert.Throws<InvalidOperationException>(() => src.Read());
    }

    [Fact]
    public void ProcessInfoReader_ReadsOwnCommandLineAndCreateTime()
    {
        var r = new ProcessInfoReader();
        int me = Environment.ProcessId;
        var ct = r.GetCreateTime(me);
        Assert.NotNull(ct);
        var id = r.GetIdentity(me, ct!.Value, "x", 0);
        Assert.Contains(".exe", id.Path, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(id.CommandLine);
    }

    [Fact]
    public void ProcessInfoReader_UnknownPid_ReturnsEmptyStrings()
    {
        var r = new ProcessInfoReader();
        var id = r.GetIdentity(int.MaxValue - 1, 5, "ghost.exe", 0);
        Assert.Equal("ghost.exe", id.Name);
        Assert.Equal("", id.Path);
        Assert.Equal("", id.CommandLine);
        Assert.Null(r.GetCreateTime(int.MaxValue - 1));
    }

    [Fact]
    public void ProcessInfoReader_CanSuppressCommandLines()
    {
        var r = new ProcessInfoReader(() => false);
        var id = r.GetIdentity(Environment.ProcessId, 1, "x", 0);
        Assert.Equal("", id.CommandLine);
        Assert.NotEmpty(id.Path);
    }
}

