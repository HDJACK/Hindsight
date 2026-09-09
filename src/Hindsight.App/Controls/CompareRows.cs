using Hindsight.Core.Analysis;

namespace Hindsight.App.Controls;

/// <summary>The metric the compare diff grid is currently showing.</summary>
public enum CompareMetric { Cpu, Gpu, Io, Net, Disk }

/// <summary>One process row of the compare diff grid. <paramref name="Worse"/> drives the red delta.</summary>
public sealed record DiffRow(
    string Process, string A, string B, string Delta,
    double ValueA, double ValueB, double AbsDelta, bool Worse);

/// <summary>One row of a compare anomaly list. <paramref name="Time"/> is relative to its own recording's start.</summary>
public sealed record CompareAnomalyRow(
    Anomaly Anomaly, string Severity, string Metric, string Time, string Duration, string Sentence);
