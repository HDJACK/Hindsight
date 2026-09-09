using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.App.Controls;

/// <summary>Row builders and list/filter plumbing for <see cref="AnalysisTabs"/>; all of it is cheap and runs on the
/// UI thread. The expensive whole-source aggregation lives in <see cref="AnalysisSource"/>.</summary>
public partial class AnalysisTabs
{
    /// <summary>Expands each explanation's contributors with the parent chain and command line resolved through the
    /// source's identities; both read "" when the identity is unknown.</summary>
    private static IReadOnlyList<AnomalyRow> BuildAnomalyRows(IReadOnlyList<Anomaly> anomalies, IIdentitySource ids)
    {
        var rows = new List<AnomalyRow>(anomalies.Count);
        foreach (var a in anomalies)
        {
            var list = a.Explanation.Contributors;
            var contributors = new List<ContributorRow>(list.Count);
            foreach (var c in list)
            {
                var identity = ids.Lookup(c.IdentityId);
                contributors.Add(new ContributorRow(
                    c.IdentityId, $"{c.Name} ({c.Pid})", Format.Percent(c.Share * 100),
                    MetricInfo.Format(a.Metric, c.Value), MetricInfo.Format(a.Metric, c.BaselineValue),
                    Delta(a.Metric, c.Value - c.BaselineValue),
                    identity is null ? "" : ParentChain.Describe(identity, ids),
                    identity?.CommandLine ?? ""));
            }

            string top = list.Count > 0 ? $"{list[0].Name} ({Format.Percent(list[0].Share * 100)})" : "";
            rows.Add(new AnomalyRow(
                a, a.Severity.ToString(), MetricInfo.Label(a.Metric), Format.Time(a.Range.FromUnix),
                Format.Duration(a.Seconds), MetricInfo.Format(a.Metric, a.PeakValue), MetricInfo.Format(a.Metric, a.BaselineValue),
                a.Explanation.Sentence, top, contributors, BuildContext(a.Explanation)));
        }
        return rows;
    }

    private static string Delta(Metric m, double d) => (d >= 0 ? "+" : "-") + MetricInfo.Format(m, Math.Abs(d));

    private static string BuildContext(Explanation e)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(e.ForegroundName)) parts.Add("Foreground: " + e.ForegroundName);
        if (e.UserIdle) parts.Add("user idle");
        if (e.OnAc is bool ac) parts.Add(ac ? "on AC" : "on battery");
        if (e.Markers.Count > 0) parts.Add("markers: " + string.Join(", ", e.Markers.Select(m => m.Text)));
        return string.Join("  ·  ", parts);
    }

    /// <summary>One toggle per metric that actually has an anomaly. A live refresh rebuilds the toggles, so the state
    /// the user set is carried over per metric; a metric that was not on screen before starts enabled.</summary>
    private void BuildMetricFilters()
    {
        var previous = new Dictionary<Metric, bool>(_metricToggles.Count);
        foreach (var old in _metricToggles)
        {
            if (old.Tag is Metric metric) previous[metric] = old.IsChecked == true;
            old.Checked -= Filter_Changed;
            old.Unchecked -= Filter_Changed;
        }
        _metricToggles.Clear();
        MetricFilters.Children.Clear();

        foreach (var m in MetricInfo.All)
        {
            bool present = false;
            foreach (var r in _anomalyRows) { if (r.Anomaly.Metric == m) { present = true; break; } }
            if (!present) continue;
            var toggle = new ToggleButton
            {
                Content = MetricInfo.Label(m),
                Tag = m,
                IsChecked = !previous.TryGetValue(m, out bool wasChecked) || wasChecked,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(10, 4, 10, 4),
            };
            toggle.Checked += Filter_Changed;
            toggle.Unchecked += Filter_Changed;
            _metricToggles.Add(toggle);
            MetricFilters.Children.Add(toggle);
        }
        ApplyAnomalyFilter();
    }

    private void ApplyAnomalyFilter()
    {
        var allowed = new HashSet<Metric>();
        foreach (var t in _metricToggles) if (t.IsChecked == true && t.Tag is Metric m) allowed.Add(m);
        bool onlyHigh = OnlyHigh.IsChecked == true;

        var rows = new List<AnomalyRow>(_anomalyRows.Count);
        foreach (var r in _anomalyRows)
        {
            if (!allowed.Contains(r.Anomaly.Metric)) continue;
            if (onlyHigh && r.Anomaly.Severity != Severity.High) continue;
            rows.Add(r);
        }

        _suppressSelection = true;
        try { AnomalyList.ItemsSource = rows; }
        finally { _suppressSelection = false; }
        UpdateAnomalyEmptyState();
    }

    private void UpdateAnomalyEmptyState()
    {
        bool empty = AnomalyList.Items.Count == 0;
        AnomalyEmpty.Text = _busy ? "Detecting…" : "No anomalies found.";
        AnomalyEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyProcessFilter()
    {
        string q = ProcessSearch.Text ?? "";
        IReadOnlyList<ProcessPick> matches;
        if (string.IsNullOrWhiteSpace(q)) matches = _picks;
        else
        {
            var found = new List<ProcessPick>();
            foreach (var p in _picks) if (p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) found.Add(p);
            matches = found;
        }
        _suppressSelection = true;
        try { ProcessMatches.ItemsSource = matches; }
        finally { _suppressSelection = false; }
    }

    /// <summary>Facts come from the context's own ticks, i.e. the snapshot detection ran on. On a live source that
    /// snapshot is up to one analysis interval behind the ring; the tab is a drill-down of the analysed data, not of
    /// "now", so it stays consistent with the anomalies and the overview shown beside it.</summary>
    private void FillProcessDetails(int identityId)
    {
        var ctx = _ctx;
        var series = ctx is null ? null : ProcessSeries.Extract(ctx.Ticks, ctx.Ids, identityId);
        if (series is null)
        {
            ProcessFacts.ItemsSource = null;
            ProcessAnomalies.Text = "";
            ProcessEmpty.Visibility = Visibility.Visible;
            return;
        }

        ProcessEmpty.Visibility = Visibility.Collapsed;
        var id = series.Identity;
        _totals.TryGetValue(identityId, out var t);
        ProcessFacts.ItemsSource = new List<FactRow>
        {
            new("Name / PID", $"{id.DisplayName} ({id.Pid})"),
            new("Path", string.IsNullOrEmpty(id.Path) ? "unknown" : id.Path),
            new("Description", string.IsNullOrEmpty(id.Description) ? "unknown" : id.Description),
            new("Company", string.IsNullOrEmpty(id.Company) ? "unknown" : id.Company),
            new("Services", string.IsNullOrEmpty(id.Services) ? "-" : id.Services),
            new("Command line", string.IsNullOrEmpty(id.CommandLine) ? "unknown" : id.CommandLine),
            new("Parent chain", series.ParentChain),
            new("First / last seen", $"{Format.Time(series.FirstSeen)} - {Format.Time(series.LastSeen)}"),
            new("CPU seconds", t is null ? "-" : t.CpuSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"),
            new("GPU max", t is null ? "-" : Format.Percent(t.GpuPercentMax)),
            new("File I/O", t is null ? "-" : Format.Bytes(t.ReadBytes + t.WriteBytes)),
            new("Network", t is null ? "-" : Format.Bytes(t.NetBytes)),
            new("Disk (phys)", t is null ? "-" : Format.Bytes(t.DiskReadBytes + t.DiskWriteBytes)),
            new("Peak working set", Format.Bytes(series.PeakWorkingSet)),
            new("Peak private bytes", t is null ? "-" : Format.Bytes(t.PeakPrivateBytes)),
            new("Peak handles / threads", $"{series.PeakHandles} / {series.PeakThreads}"),
        };

        var involved = new List<string>();
        foreach (var r in _anomalyRows)
        {
            foreach (var c in r.Anomaly.Explanation.Contributors)
            {
                if (c.IdentityId != identityId) continue;
                involved.Add("· " + r.Sentence);
                break;
            }
        }
        ProcessAnomalies.Text = involved.Count == 0
            ? "Contributed to 0 anomalies."
            : $"Contributed to {involved.Count} {(involved.Count == 1 ? "anomaly" : "anomalies")}:" + Environment.NewLine
              + string.Join(Environment.NewLine, involved);
    }
}
