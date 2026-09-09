using Hindsight.Core.Analysis;
using Hindsight.Core.Model;

namespace Hindsight.Core.Tests;

public class AnomalyKeyTests
{
    private static Anomaly Make(Metric metric = Metric.Cpu, long from = 100, long to = 110,
        double peak = 50, double baseline = 10, Severity severity = Severity.High, int seconds = 11) =>
        new(metric, new TimeRange(from, to), peak, baseline, severity, seconds, Explanation.Empty);

    [Fact]
    public void SameMetricAndRange_MatchesAcrossReDetection()
    {
        // A re-run over a rolled window keeps the metric and the range but moves everything else.
        var a = Make();
        var b = Make(peak: 61, baseline: 12, severity: Severity.Medium, seconds: 9);
        Assert.False(a == b);                 // record equality does not carry the selection
        Assert.True(AnomalyKey.Equals(a, b)); // the key does
    }

    [Fact]
    public void DifferentMetricOrRange_DoesNotMatch()
    {
        var a = Make();
        Assert.False(AnomalyKey.Equals(a, Make(metric: Metric.Network)));
        Assert.False(AnomalyKey.Equals(a, Make(from: 101)));
        Assert.False(AnomalyKey.Equals(a, Make(to: 111)));
    }

    [Fact]
    public void NullsAndSameInstance()
    {
        var a = Make();
        Assert.True(AnomalyKey.Equals(a, a));
        Assert.True(AnomalyKey.Equals(null, null));
        Assert.False(AnomalyKey.Equals(a, null));
        Assert.False(AnomalyKey.Equals(null, a));
    }
}
