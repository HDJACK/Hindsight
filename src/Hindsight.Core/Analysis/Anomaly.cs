using Hindsight.Core.Model;

namespace Hindsight.Core.Analysis;

/// <summary>How far a detection strayed from its baseline.</summary>
public enum Severity { Low, Medium, High }

/// <summary>One process's share of a metric during an anomaly, with its own average and baseline values.</summary>
public sealed record Contributor(int IdentityId, string Name, int Pid, double Share, double Value, double BaselineValue);

/// <summary>What surrounded an anomaly: the top contributors, the machine's state, nearby markers, and a sentence describing them.</summary>
public sealed record Explanation(IReadOnlyList<Contributor> Contributors, string? ForegroundName, bool UserIdle, bool? OnAc, IReadOnlyList<Marker> Markers, string Sentence)
{
    public static readonly Explanation Empty = new(Array.Empty<Contributor>(), null, false, null, Array.Empty<Marker>(), "");
}

/// <summary><c>DurationSeconds</c> is the summed sample interval of the ticks in the run, which is what the
/// minimum-duration rule tests; it only equals <c>Range.Seconds</c> (the unix span) when the sample interval is 1 s.</summary>
public sealed record Anomaly(Metric Metric, TimeRange Range, double PeakValue, double BaselineValue, Severity Severity, int DurationSeconds, Explanation Explanation)
{
    public int Seconds => DurationSeconds;
}
