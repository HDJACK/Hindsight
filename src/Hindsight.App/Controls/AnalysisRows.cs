using Hindsight.Core.Analysis;

namespace Hindsight.App.Controls;

/// <summary>One tile in the Overview/Range summary strip.</summary>
public sealed record SummaryItem(string Title, string Value, string Detail);

/// <summary>One process row of the Overview/Range grid. The share values are 0–100 so a ProgressBar can bind to them directly.</summary>
public sealed record ShareRow(
    int IdentityId, string Process,
    double Cpu, double Gpu, double Io, double Disk, double Net,
    string CpuText, string GpuText, string IoText, string DiskText, string NetText,
    string Score, string Seconds, double ScoreValue, long SecondsValue);

/// <summary>One row of the Anomalies list, with its pre-formatted explanation.</summary>
public sealed record AnomalyRow(
    Anomaly Anomaly, string Severity, string Metric, string Time, string Duration,
    string Peak, string Baseline, string Sentence, string TopContributor,
    IReadOnlyList<ContributorRow> Contributors, string Context);

/// <summary>One process inside an anomaly's explanation. ParentChain and CommandLine are "" when the identity is
/// no longer known to the source.</summary>
public sealed record ContributorRow(
    int IdentityId, string Process, string Share, string Value, string Baseline, string Delta,
    string ParentChain, string CommandLine);

/// <summary>One entry in the Process tab's picker list.</summary>
public sealed record ProcessPick(int IdentityId, string Display, string Name);

/// <summary>One label/value line in the Process tab's detail card.</summary>
public sealed record FactRow(string Label, string Value);
