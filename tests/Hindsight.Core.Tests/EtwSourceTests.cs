using Hindsight.Core.Context;
using Hindsight.Core.Etw;
using Hindsight.Core.Settings;
using Microsoft.Diagnostics.Tracing.Session;

namespace Hindsight.Core.Tests;

public class EtwSourceTests
{
    [Fact]
    public void CyclesPerSecond_IsPlausible()
    {
        double cps = CycleCalibrator.CyclesPerSecond();
        Assert.InRange(cps, 5e8, 1e11);
    }

    [Fact]
    public void Start_WithoutElevation_ThrowsEtwUnavailable()
    {
        if (TraceEventSession.IsElevated() == true) return; // as admin this case cannot be tested
        using var src = new EtwSource();
        Assert.Throws<EtwUnavailableException>(() => src.Start());
    }

    [Fact]
    public void Read_BeforeStart_ReturnsEmptySnapshot()
    {
        using var src = new EtwSource();
        var s = src.Read();
        Assert.Empty(s.Deltas);
        Assert.Empty(s.Started);
        Assert.Empty(s.Exited);
    }

    [Fact]
    public void Constructor_AcceptsContextAndSettings()
    {
        using var src = new EtwSource(null, new ContextAccumulator(), () => AppSettings.Default);
        Assert.False(src.FilePathsDisabled);
    }
}
