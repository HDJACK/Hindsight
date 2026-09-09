using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;

namespace Hindsight.App.Controls;

/// <summary>Side-by-side view of two recordings: a header per recording, two synchronised timelines,
/// a per-process diff table for one metric at a time, and the anomalies of each recording.</summary>
public partial class CompareView : UserControl
{
    private const int EasyRowLimit = 10;

    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static readonly IReadOnlyList<CompareMetric> Metrics = new[]
    {
        CompareMetric.Cpu, CompareMetric.Gpu, CompareMetric.Io, CompareMetric.Net, CompareMetric.Disk,
    };

    private static readonly IReadOnlyList<LaneKind> EasyLanes = new[]
    {
        LaneKind.Cpu, LaneKind.Gpu, LaneKind.FileIo, LaneKind.RamFree,
    };

    private Recording? _a;
    private Recording? _b;
    private ComparisonResult? _result;
    private bool _syncing;
    private bool _suppressSelection;
    private DetailLevel _detailLevel = AppHost.DetailLevel;

    public CompareView()
    {
        InitializeComponent();
        foreach (var m in Metrics) MetricPicker.Items.Add(Label(m));
        MetricPicker.SelectedIndex = 0;
        TimelineA.ViewChanged += () => Sync(TimelineA, TimelineB);
        TimelineB.ViewChanged += () => Sync(TimelineB, TimelineA);
        ApplyDetailLevel(_detailLevel);
    }

    /// <summary>Raised when the user picks an anomaly; the recording is the one the anomaly belongs to.
    /// Both timelines are already zoomed to it when this fires.</summary>
    public event Action<Recording, Anomaly>? AnomalySelected;

    /// <summary>Fills the whole view. Everything here is synchronous; the comparison itself is computed by the caller.</summary>
    public void Show(Recording a, Recording b, ComparisonResult result)
    {
        _a = a; _b = b; _result = result;

        TitleA.Text = "A: " + a.Meta.Name;
        TitleB.Text = "B: " + b.Meta.Name;
        MetaA.Text = MetaLine(a);
        MetaB.Text = MetaLine(b);
        SummaryA.Text = SummaryLine(result.SummaryA);
        SummaryB.Text = SummaryLine(result.SummaryB);

        Prepare(TimelineA, a, result.AnomaliesA);
        Prepare(TimelineB, b, result.AnomaliesB);

        _suppressSelection = true;
        try
        {
            AnomaliesA.ItemsSource = BuildAnomalyRows(result.AnomaliesA, a.Meta.StartUnix);
            AnomaliesB.ItemsSource = BuildAnomalyRows(result.AnomaliesB, b.Meta.StartUnix);
        }
        finally { _suppressSelection = false; }

        AnomalyHeaderA.Text = $"Anomalies in A ({result.AnomaliesA.Count})";
        AnomalyHeaderB.Text = $"Anomalies in B ({result.AnomaliesB.Count})";
        AnomaliesEmptyA.Visibility = result.AnomaliesA.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AnomaliesEmptyB.Visibility = result.AnomaliesB.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // The comparison pairs the two recordings sample by sample; with unequal intervals that is not the same as
        // pairing them by elapsed time, and the reader has to be told rather than left to infer it from the meta lines.
        bool sameInterval = a.Meta.IntervalSeconds == b.Meta.IntervalSeconds;
        IntervalNote.Text = sameInterval ? "" :
            $"Different sample intervals ({a.Meta.IntervalSeconds} s vs {b.Meta.IntervalSeconds} s): timelines are aligned by sample, not by time.";
        IntervalNote.Visibility = sameInterval ? Visibility.Collapsed : Visibility.Visible;

        ApplyDetailLevel(_detailLevel);   // also rebuilds the diff grid with the right row limit

        // Fit both without letting the first Fit push its view onto the other.
        _syncing = true;
        try { TimelineA.Fit(); TimelineB.Fit(); }
        finally { _syncing = false; }
    }

    /// <summary>Easy hides both timelines and the anomaly lists, leaving the header and the diff table.</summary>
    public void ApplyDetailLevel(DetailLevel level)
    {
        _detailLevel = level;
        bool pro = level == DetailLevel.Professional;
        var vis = pro ? Visibility.Visible : Visibility.Collapsed;
        var zero = new GridLength(0);

        TimelineA.Visibility = vis;
        TimelineB.Visibility = vis;
        RowTimelineA.Height = pro ? GridLength.Auto : zero;
        RowTimelineB.Height = pro ? GridLength.Auto : zero;

        AnomalyPanel.Visibility = vis;
        RowAnomalies.Height = pro ? new GridLength(1, GridUnitType.Star) : zero;
        RowAnomalies.MinHeight = pro ? 120 : 0;

        var s = AppHost.Settings.Current;
        var kinds = pro ? s.VisibleLaneKinds() : EasyLanes;
        TimelineA.SetLanes(kinds);
        TimelineB.SetLanes(kinds);
        TimelineA.ShowMiniMap = pro;
        TimelineB.ShowMiniMap = pro;
        bool context = pro && s.ShowForegroundApp;
        var a = _a; var b = _b;
        TimelineA.SetContext(context && a is not null, pid => a?.ByPid(pid)?.DisplayName);
        TimelineB.SetContext(context && b is not null, pid => b?.ByPid(pid)?.DisplayName);

        RebuildDiff();
    }

    /// <summary>Shows the indeterminate bar above the timelines while the comparison runs.</summary>
    public void SetBusy(bool busy)
    {
        var vis = busy ? Visibility.Visible : Visibility.Collapsed;
        Busy.Visibility = vis;
        BusyText.Visibility = vis;
    }

    private static string Label(CompareMetric m) => m switch
    {
        CompareMetric.Cpu => "CPU seconds",
        CompareMetric.Gpu => "GPU max %",
        CompareMetric.Io => "File I/O",
        CompareMetric.Net => "Network",
        _ => "Disk (phys)",
    };

    private static string MetaLine(Recording r) =>
        $"{DateTimeOffset.FromUnixTimeSeconds(r.Meta.StartUnix).ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Format.Duration(r.Meta.DurationSeconds)}";

    private static string SummaryLine(SystemSummary s)
    {
        var cpu = s.Stat(Metric.Cpu);
        var gpu = s.Stat(Metric.Gpu);
        var ram = s.Stat(Metric.RamFree);
        return $"CPU avg {MetricInfo.Format(Metric.Cpu, cpu.Avg)} · peak {MetricInfo.Format(Metric.Cpu, cpu.Peak)}"
             + $"  ·  GPU avg {MetricInfo.Format(Metric.Gpu, gpu.Avg)}"
             // RamFree is LowerIsWorse, so Peak holds the minimum over the recording.
             + $"  ·  RAM free min {MetricInfo.Format(Metric.RamFree, ram.Peak)}";
    }

    private static void Prepare(TimelineControl timeline, Recording r, IReadOnlyList<Anomaly> anomalies)
    {
        timeline.TimeOrigin = r.Meta.StartUnix;
        timeline.AllowMarkerDeletion = false;
        timeline.SetWindow(0);
        timeline.SetOverlay(null);
        timeline.HighlightedAnomaly = null;
        timeline.SetRange(null);
        timeline.SetData(r.Ticks, r.Markers);
        timeline.SetAnomalies(anomalies);
    }

    private static IReadOnlyList<CompareAnomalyRow> BuildAnomalyRows(IReadOnlyList<Anomaly> anomalies, long origin)
    {
        var rows = new List<CompareAnomalyRow>(anomalies.Count);
        foreach (var a in anomalies)
            rows.Add(new CompareAnomalyRow(
                a, a.Severity.ToString(), MetricInfo.Label(a.Metric),
                "+" + Format.Duration(a.Range.FromUnix - origin), Format.Duration(a.Seconds),
                a.Explanation.Sentence));
        return rows;
    }

    /// <summary>Rebuilds the diff grid for the picked metric, sorted by |Δ| descending.</summary>
    private void RebuildDiff()
    {
        var result = _result;
        int index = MetricPicker.SelectedIndex;
        var metric = index >= 0 && index < Metrics.Count ? Metrics[index] : CompareMetric.Cpu;
        if (result is null)
        {
            DiffGrid.ItemsSource = Array.Empty<DiffRow>();
            DiffEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        var rows = new List<DiffRow>(result.Rows.Count);
        foreach (var r in result.Rows)
        {
            var (a, b) = Values(r, metric);
            double delta = b - a;
            rows.Add(new DiffRow(
                r.Name, Value(metric, a), Value(metric, b), Delta(metric, delta),
                a, b, Math.Abs(delta), delta > 0));
        }
        rows.Sort((x, y) => y.AbsDelta.CompareTo(x.AbsDelta));

        int limit = _detailLevel == DetailLevel.Professional ? rows.Count : Math.Min(EasyRowLimit, rows.Count);
        DiffGrid.ItemsSource = rows.GetRange(0, limit);
        DiffEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Easy shows only the largest differences; say so rather than truncating the table silently.
        bool truncated = limit < rows.Count;
        DiffTruncated.Text = truncated ? $"Showing top {limit} of {rows.Count}" : "";
        DiffTruncated.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
    }

    private static (double A, double B) Values(CompareRow r, CompareMetric m) => m switch
    {
        CompareMetric.Cpu => (r.CpuA, r.CpuB),
        CompareMetric.Gpu => (r.GpuA, r.GpuB),
        CompareMetric.Io => (r.IoA, r.IoB),
        CompareMetric.Net => (r.NetA, r.NetB),
        _ => (r.DiskA, r.DiskB),
    };

    private static string Value(CompareMetric m, double v) => m switch
    {
        CompareMetric.Cpu => v.ToString("0.0", En) + " s",
        CompareMetric.Gpu => Format.Percent(v),
        _ => MetricInfo.Bytes(v),
    };

    private static string Delta(CompareMetric m, double v) => (v >= 0 ? "+" : "-") + Value(m, Math.Abs(v));

    /// <summary>Mirrors one timeline's view window onto the other. The guard keeps the two from ping-ponging.</summary>
    private void Sync(TimelineControl from, TimelineControl to)
    {
        if (_syncing) return;
        _syncing = true;
        try { to.SetView(from.ViewFrom, from.ViewSlots); }
        finally { _syncing = false; }
    }

    private void MetricPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildDiff();

    private void AnomaliesA_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OnAnomalyPicked(AnomaliesA.SelectedItem as CompareAnomalyRow, fromA: true);

    private void AnomaliesB_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OnAnomalyPicked(AnomaliesB.SelectedItem as CompareAnomalyRow, fromA: false);

    /// <summary>Zooms both timelines to the anomaly, translating its absolute range into the other recording's own clock.</summary>
    private void OnAnomalyPicked(CompareAnomalyRow? row, bool fromA)
    {
        if (_suppressSelection || row is null) return;
        var a = _a; var b = _b;
        if (a is null || b is null) return;

        _suppressSelection = true;
        try { (fromA ? AnomaliesB : AnomaliesA).SelectedItem = null; }
        finally { _suppressSelection = false; }

        var owner = fromA ? a : b;
        var other = fromA ? b : a;
        var ownerTimeline = fromA ? TimelineA : TimelineB;
        var otherTimeline = fromA ? TimelineB : TimelineA;
        long shift = other.Meta.StartUnix - owner.Meta.StartUnix;

        _syncing = true;
        try
        {
            ownerTimeline.ZoomTo(row.Anomaly.Range.FromUnix, row.Anomaly.Range.ToUnix);
            otherTimeline.ZoomTo(row.Anomaly.Range.FromUnix + shift, row.Anomaly.Range.ToUnix + shift);
        }
        finally { _syncing = false; }

        ownerTimeline.HighlightedAnomaly = row.Anomaly;
        otherTimeline.HighlightedAnomaly = null;
        AnomalySelected?.Invoke(owner, row.Anomaly);
    }
}
