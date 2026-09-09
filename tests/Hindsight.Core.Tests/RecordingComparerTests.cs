using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class RecordingComparerTests
{
    [Fact]
    public void Join_ByNameSumsAndKeepsMissingSide()
    {
        var a = new[]
        {
            new ProcessTotals(0, "x.exe", 1, 2.0, 10, 0, 5, 0, 1, 0, 0, 30, 0, 0, 0),
            new ProcessTotals(1, "x.exe", 9, 1.0, 10, 0, 0, 0, 1, 0, 0, 50, 0, 0, 0),
            new ProcessTotals(2, "only-a.exe", 2, 0.5, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0),
        };
        var b = new[] { new ProcessTotals(0, "X.EXE", 3, 4.0, 0, 20, 7, 0, 1, 0, 0, 10, 0, 0, 0) };
        var rows = RecordingComparer.Join(a, b);
        Assert.Equal(2, rows.Count);
        var x = rows[0];
        Assert.Equal("x.exe", x.Name); Assert.Equal(3.0, x.CpuA); Assert.Equal(4.0, x.CpuB); Assert.Equal(1.0, x.CpuDelta);
        Assert.Equal(20, x.IoA); Assert.Equal(20, x.IoB); Assert.Equal(50, x.GpuA); Assert.Equal(10, x.GpuB);
        Assert.Equal("only-a.exe", rows[1].Name); Assert.Equal(0, rows[1].CpuB);
    }

    [Fact]
    public void Compare_ProducesSummariesAndAnomalies()
    {
        var ta = Flat(120); for (int i = 50; i < 60; i++) ta[i].CpuTotalPercent = 90;
        var tb = Flat(120);
        var result = RecordingComparer.Compare(AsRecording(ta), AsRecording(tb), AppSettings.Default with { BaselineWindowSeconds = 60 });
        Assert.Equal(120, result.SummaryA.Seconds);
        Assert.Equal(90, result.SummaryA.Stat(Metric.Cpu).Peak);
        Assert.Single(result.AnomaliesA); Assert.Empty(result.AnomaliesB);
        Assert.Single(result.Rows);
    }
}
