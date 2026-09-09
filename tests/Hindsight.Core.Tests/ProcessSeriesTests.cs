using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class ProcessSeriesTests
{
    [Fact]
    public void Extract_AlignsPointsWithTicks()
    {
        var ticks = new List<Tick> { Tick(100, 10, S(0, cpu: 5, ws: 100)), Tick(101, 10), Tick(102, 10, S(2, cpu: 7, ws: 300, net: 9)) };
        ticks[2].Samples[0].HandleCount = 40; ticks[2].Samples[0].ThreadCount = 8;
        ticks[2].Samples[0].WriteBytes = 5; ticks[2].Samples[0].ReadBytes = 3;
        var r = AsRecording(ticks);
        var s = ProcessSeries.Extract(ticks, r, 2)!;
        Assert.Equal(3, s.Points.Count);
        Assert.Null(s.Points[0]); Assert.Null(s.Points[1]);
        Assert.Equal(7, s.Points[2]!.Value.Cpu);
        Assert.Equal(102, s.FirstSeen); Assert.Equal(102, s.LastSeen);
        Assert.Equal(40, s.PeakHandles); Assert.Equal(8, s.PeakThreads); Assert.Equal(300, s.PeakWorkingSet);
        Assert.Equal("a.exe > c.exe", s.ParentChain);
        Assert.Equal(9, s.Value(2, LaneKind.Network)); Assert.Equal(0, s.Value(0, LaneKind.Cpu));
        Assert.Equal(8, s.Points[2]!.Value.FileIo); Assert.Equal(8, s.Value(2, LaneKind.FileIo));
        Assert.Equal(new[] { 2 }, s.IndexesWithSamples);
        Assert.Null(ProcessSeries.Extract(ticks, r, 1));
    }

    /// <summary>The source range is what lets an index-aligned consumer notice that the ticks moved underneath it,
    /// which an equal Points.Count cannot show once a live ring is full.</summary>
    [Fact]
    public void Extract_RecordsTheSourceTickRange()
    {
        var ticks = new List<Tick> { Tick(100, 10, S(0, cpu: 5)), Tick(101, 10, S(0, cpu: 5)), Tick(102, 10, S(0, cpu: 5)) };
        var r = AsRecording(ticks);
        var s = ProcessSeries.Extract(ticks, r, 0)!;
        Assert.Equal(100, s.SourceFirstUnix);
        Assert.Equal(102, s.SourceLastUnix);

        // The ring advanced by one second: same count, different range.
        var moved = new List<Tick> { Tick(101, 10, S(0, cpu: 5)), Tick(102, 10, S(0, cpu: 5)), Tick(103, 10, S(0, cpu: 5)) };
        var s2 = ProcessSeries.Extract(moved, AsRecording(moved), 0)!;
        Assert.Equal(s.Points.Count, s2.Points.Count);
        Assert.NotEqual(s.SourceFirstUnix, s2.SourceFirstUnix);
    }
}
