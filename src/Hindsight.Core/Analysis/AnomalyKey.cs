namespace Hindsight.Core.Analysis;

/// <summary>Identity of a detection across re-runs. Live analysis re-detects every few seconds, and the peak,
/// baseline, severity and explanation all move as the window rolls, so a selection or highlight can only be carried
/// over by the metric and the unix range.</summary>
public static class AnomalyKey
{
    /// <summary>True when both describe the same detection: the same metric over the same unix range. Two nulls are equal.</summary>
    public static bool Equals(Anomaly? a, Anomaly? b) =>
        ReferenceEquals(a, b)
        || (a is not null && b is not null
            && a.Metric == b.Metric && a.Range.FromUnix == b.Range.FromUnix && a.Range.ToUnix == b.Range.ToUnix);
}
