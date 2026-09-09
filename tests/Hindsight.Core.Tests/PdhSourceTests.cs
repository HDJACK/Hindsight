using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class PdhSourceTests
{
    [Fact]
    public void Read_BeforeStart_ReturnsEmptySnapshot()
    {
        using var src = new PdhSource();
        var s = src.Read();
        Assert.Empty(s.Deltas);
        Assert.Empty(s.Started);
        Assert.Empty(s.Exited);
    }

    [Fact]
    public void Read_ReturnsCommitPercentAndCollects()
    {
        using var src = new PdhSource();
        src.Start();
        _ = src.Read();
        Thread.Sleep(300);
        var snap = src.Read();

        Assert.InRange(snap.Totals.CommitPercent, 0.0001f, 100f);
        Assert.DoesNotContain(src.MissingCounters, m => m.Contains("Committed Bytes"));
    }
}
