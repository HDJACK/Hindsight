using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;
using static Hindsight.Core.Tests.AnalysisTestData;

namespace Hindsight.Core.Tests;

public class ExplanationBuilderTests
{
    private static (Recording r, Anomaly a) Scenario()
    {
        var ticks = Flat(60);
        for (int i = 40; i < 50; i++)
        {
            ticks[i].CpuTotalPercent = 92;
            ticks[i].Samples = new[] { S(0, cpu: 71), S(1, cpu: 12), S(2, cpu: 9) };
            ticks[i].ForegroundPid = 2;
            ticks[i].Flags |= TickFlags.OnAc;
        }
        ticks[45].Flags |= TickFlags.UserIdle;
        var markers = new[] { new Marker(1044, MarkerKind.Manual, "Build started"), new Marker(1010, MarkerKind.Manual, "outside") };
        var r = AsRecording(ticks, markers);
        var a = new Anomaly(Metric.Cpu, new TimeRange(1040, 1049), 92, 10, Severity.High, 10, Explanation.Empty);
        return (r, a);
    }

    [Fact]
    public void Build_ContributorsForegroundIdleMarkers()
    {
        var (r, a) = Scenario();
        var e = ExplanationBuilder.Build(a, r.Ticks, r, r.Markers, 30);
        Assert.Equal(3, e.Contributors.Count);
        Assert.Equal("a.exe", e.Contributors[0].Name);
        Assert.Equal(71.0 / 92, e.Contributors[0].Share, 3);
        Assert.Equal(71, e.Contributors[0].Value, 3);
        Assert.Equal(5, e.Contributors[0].BaselineValue, 3);     // a.exe ran at 5 % before
        Assert.Equal(0, e.Contributors[1].BaselineValue, 3);     // b.exe unseen before
        Assert.Equal("b.exe", e.ForegroundName);
        Assert.True(e.UserIdle);
        Assert.True(e.OnAc);
        Assert.Single(e.Markers);
        Assert.Equal("Build started", e.Markers[0].Text);
        Assert.StartsWith("CPU 92.0 % for 10 s (baseline 10.0 %): a.exe 77 %, b.exe 13 %, c.exe 10 %; you were in b.exe; you were idle; marker 'Build started' at ", e.Sentence);
    }

    [Fact]
    public void Build_NoHostState_GivesNullOnAcAndNoForeground()
    {
        var ticks = Flat(20);
        var r = AsRecording(ticks);
        var a = new Anomaly(Metric.Cpu, new TimeRange(1005, 1006), 50, 10, Severity.High, 2, Explanation.Empty);
        var e = ExplanationBuilder.Build(a, r.Ticks, r, r.Markers, 30);
        Assert.Null(e.OnAc); Assert.Null(e.ForegroundName); Assert.False(e.UserIdle);
        Assert.Equal("CPU 50.0 % for 2 s (baseline 10.0 %): a.exe 100 %", e.Sentence);
    }

    [Fact]
    public void Sentence_CpuMhzAndDeltaWording()
    {
        var mhz = new Anomaly(Metric.CpuMhz, new TimeRange(1, 10), 1200, 5000, Severity.High, 10, Explanation.Empty);
        var c = new[] { new Contributor(0, "a.exe", 1, 0.6, 40, 5) };
        Assert.Equal("CPU MHz down to 1200 MHz for 10 s (baseline 5000 MHz): a.exe 60 %",
            ExplanationBuilder.Sentence(mhz, c, null, false, Array.Empty<Marker>()));

        var procs = new Anomaly(Metric.ProcessCount, new TimeRange(1, 5), 360, 300, Severity.Medium, 5, Explanation.Empty);
        Assert.Equal("Processes up to 360 for 5 s (baseline 300)",
            ExplanationBuilder.Sentence(procs, Array.Empty<Contributor>(), null, false, Array.Empty<Marker>()));
    }

    [Fact]
    public void SampleValue_NewMetricContributors()
    {
        var s = S(0, cpu: 12, gpuMem: 700L << 20);
        Assert.Equal(12, ExplanationBuilder.SampleValue(in s, Metric.CpuMhz), 3);
        Assert.Equal(12, ExplanationBuilder.SampleValue(in s, Metric.ProcessCount), 3);
        Assert.Equal(700L << 20, ExplanationBuilder.SampleValue(in s, Metric.GpuMemory));
    }

    [Fact]
    public void Sentence_RamFreeWording()
    {
        var a = new Anomaly(Metric.RamFree, new TimeRange(1, 5), 1L << 30, 8L << 30, Severity.High, 5, Explanation.Empty);
        var c = new[] { new Contributor(0, "a.exe", 1, 0.6, 0, 0) };
        Assert.Equal("RAM free down to 1 GB for 5 s (baseline 8 GB): largest: a.exe 60 %", ExplanationBuilder.Sentence(a, c, null, false, Array.Empty<Marker>()));
    }

    [Fact]
    public void DetectAndExplain_FillsExplanations()
    {
        var (r, _) = Scenario();
        var list = AnomalyDetector.DetectAndExplain(r.Ticks, r, r.Markers, AppSettings.Default with { BaselineWindowSeconds = 30 });
        var cpu = list.First(x => x.Metric == Metric.Cpu);
        Assert.NotEmpty(cpu.Explanation.Contributors);
        Assert.Contains("a.exe", cpu.Explanation.Sentence);
    }

    [Fact]
    public void Sentence_UsesDisplayNameWithServices()
    {
        var (r, a) = Scenario();
        var ids = r.Identities.Select((id, i) => i == 0 ? id with { Services = "wuauserv" } : id).ToArray();
        var withServices = new Recording(r.Meta, r.Ticks, ids, r.Markers);
        var e = ExplanationBuilder.Build(a, withServices.Ticks, withServices, withServices.Markers, 30);
        Assert.Equal("a.exe (wuauserv)", e.Contributors[0].Name);
        Assert.Contains("a.exe (wuauserv) ", e.Sentence);
    }
}
