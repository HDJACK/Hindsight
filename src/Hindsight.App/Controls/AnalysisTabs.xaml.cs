using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Hindsight.Core.Analysis;
using Hindsight.Core.Context;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;

namespace Hindsight.App.Controls;

public enum AnalysisTab { Second, Overview, Range, Anomalies, Process, Context }

/// <summary>Everything the tabs need to render. Anomalies are detected outside the control (see <see cref="Services.AnalysisRunner"/>).</summary>
public sealed record AnalysisContext(
    IReadOnlyList<Tick> Ticks, IIdentitySource Ids, int CoreCount,
    IReadOnlyList<Marker> Markers, IReadOnlyList<Anomaly> Anomalies,
    // Context: the live store or the opened recording; null when the source never had one (an empty or v1/v2 source).
    IContextSource? Context = null);

/// <summary>The analysis surface below the timeline: the per-second culprits, a whole-recording overview,
/// the selected range, detected anomalies with their explanation, and a per-process drill-down.</summary>
public partial class AnalysisTabs : UserControl
{
    internal const int TopN = 10;

    private AnalysisContext? _ctx;
    private TimeRange? _range;
    private Tick? _secondTick;
    private IReadOnlyList<ShareRow> _overviewRows = Array.Empty<ShareRow>();
    private IReadOnlyList<ShareRow> _rangeRows = Array.Empty<ShareRow>();
    private IReadOnlyList<AnomalyRow> _anomalyRows = Array.Empty<AnomalyRow>();
    private readonly List<ToggleButton> _metricToggles = new();
    private IReadOnlyDictionary<int, ProcessTotals> _totals = new Dictionary<int, ProcessTotals>();
    private IReadOnlyList<ProcessPick> _picks = Array.Empty<ProcessPick>();
    private bool _suppressSelection;
    private bool _suppressFilter;
    private bool _busy;
    private int _rowLimit = TopN;
    private DetailLevel _detailLevel = AppHost.DetailLevel;

    public AnalysisTabs()
    {
        InitializeComponent();
        // The hosted CulpritsPanel subscribes to the global detail-level event itself, so this control only handles its own tabs.
        AppHost.DetailLevelChanged += () => ApplyDetailLevel(AppHost.DetailLevel);
        // The culprits grid drives the Context tab: picking a row re-fills it for that process and the shown second.
        SecondPanel.ProcessSelected += _ => RefreshContext();
        ApplyDetailLevel(AppHost.DetailLevel);
    }

    /// <summary>The per-second culprits view hosted in tab 0.</summary>
    public CulpritsPanel Second => SecondPanel;

    /// <summary>Raised when the user picks a row in the anomalies list.</summary>
    public event Action<Anomaly>? AnomalySelected;

    /// <summary>Raised when the user picks a process to overlay, or clears the overlay (null).</summary>
    public event Action<int?>? ProcessSelected;

    /// <summary>Raised by the Range tab's "Use view as range" button.</summary>
    public event Action? UseViewAsRangeRequested;

    /// <summary>Aggregates <paramref name="ctx"/> on the calling thread and binds it. Prefer the
    /// <see cref="SetSource(AnalysisSource)"/> overload with a source <see cref="Services.AnalysisRunner"/> already
    /// built in the background; this one is for the cheap cases (an empty or just-switched source).</summary>
    public void SetSource(AnalysisContext ctx) => SetSource(AnalysisSource.Build(ctx, AppHost.Settings.Current));

    /// <summary>Binds an already-aggregated source: the Overview, the Anomalies list and the process picker.
    /// The selected tab is kept. Nothing here walks the ticks except the anomaly rows.</summary>
    public void SetSource(AnalysisSource source)
    {
        var ctx = source.Context;
        // A different identity source means a different recording or a fresh live pipeline: identity ids and
        // seconds do not carry across, so the shown second and the culprits rows (which drive the Context tab)
        // have to go before the new source is bound.
        bool sourceSwitched = _ctx is not null && !ReferenceEquals(_ctx.Ids, ctx.Ids);
        // Live detection re-runs every few seconds and rebuilds every row below, which would drop the user's
        // selection (and its explanation) mid-inspection; remember it so it can be put back.
        var selected = sourceSwitched ? null : (AnomalyList.SelectedItem as AnomalyRow)?.Anomaly;
        _ctx = ctx;
        if (sourceSwitched) ShowSecond(null, ctx.Ids);
        bool any = ctx.Ticks.Count > 0;

        _totals = source.Totals;
        OverviewSummary.ItemsSource = source.Summary;
        _overviewRows = source.Rows;
        OverviewEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;

        _anomalyRows = BuildAnomalyRows(ctx.Anomalies, ctx.Ids);
        BuildMetricFilters();
        RestoreAnomalySelection(selected);

        _picks = source.Picks;
        ApplyProcessFilter();

        // The range grid was computed from the previous source; recompute it (or clear it) for the new one.
        SetRange(_range);
        // The context source may have changed with the rest of the source; re-resolve the shown second against it.
        RefreshContext();
        ApplyDetailLevel(_detailLevel);
    }

    /// <summary>Recomputes the Range tab. A null range shows the "select a range" empty state.</summary>
    public void SetRange(TimeRange? range)
    {
        _range = range;
        var ctx = _ctx;
        if (ctx is null || range is null || ctx.Ticks.Count == 0)
        {
            RangeTitle.Text = "Shift+drag in the timeline to select a range, or";
            RangeSummaryItems.ItemsSource = Array.Empty<SummaryItem>();
            _rangeRows = Array.Empty<ShareRow>();
            RangeGrid.ItemsSource = Array.Empty<ShareRow>();
            return;
        }

        var r = range.Value;
        RangeTitle.Text = $"{Format.Time(r.FromUnix)} – {Format.Time(r.ToUnix)}  ·  {Format.Duration(r.Seconds)}";
        var inRange = ctx.Anomalies.Where(a => a.Range.ToUnix >= r.FromUnix && a.Range.FromUnix <= r.ToUnix).ToList();
        // Range-limited and user-triggered, so this stays synchronous; it is still a single PerProcess pass.
        RangeSummaryItems.ItemsSource = AnalysisSource.BuildSummary(ctx.Ticks, r, inRange);
        _rangeRows = AnalysisSource.BuildShareRows(ctx, r, AppHost.Settings.Current);
        RangeGrid.ItemsSource = _rangeRows.Take(_rowLimit).ToList();
    }

    /// <summary>Forwards to the hosted culprits panel and re-fills the Context tab for the shown second. Switching to
    /// the Second tab is the caller's decision. <paramref name="ids"/> lets a page show a second before the first
    /// <see cref="SetSource(AnalysisSource)"/> has arrived; without it the bound source's identities are used.</summary>
    public void ShowSecond(Tick? tick, IIdentitySource? ids = null)
    {
        var source = ids ?? _ctx?.Ids;
        if (source is null) return;
        _secondTick = tick;
        SecondPanel.Show(tick, source);
        RefreshContext();
    }

    /// <summary>Fills the Context tab with the DNS names, endpoints and file paths recorded for the identity's pid in
    /// the minute containing <paramref name="unixTime"/>. Safe to call for a source that recorded no context.</summary>
    public void ShowContext(long unixTime, int identityId)
    {
        var ctx = _ctx;
        if (ctx is null) { ClearContext("Select a second and a process."); return; }
        if (ctx.Context is null || !ctx.Context.HasContext) { ClearContext("Not recorded in this file."); return; }
        var id = ctx.Ids.Lookup(identityId);
        if (id is null) { ClearContext("Select a second and a process."); return; }

        ContextHeader.Text = $"{id.DisplayName}  ·  {Format.TimeShort(unixTime)} minute";
        var entries = ctx.Context.ForMinute(ContextEntry.MinuteOf(unixTime), id.Pid);
        var dns = new List<ContextRow>();
        var endpoints = new List<ContextRow>();
        var files = new List<ContextRow>();
        foreach (var e in entries)
        {
            switch (e.Kind)
            {
                case ContextKind.Dns: dns.Add(new ContextRow(e.Key, e.Value.ToString(CultureInfo.InvariantCulture))); break;
                case ContextKind.Endpoint: endpoints.Add(new ContextRow(e.Key, Format.Bytes(e.Value))); break;
                default: files.Add(new ContextRow(e.Key, Format.Bytes(e.Value))); break;
            }
        }
        DnsList.ItemsSource = dns;
        EndpointList.ItemsSource = endpoints;
        FileList.ItemsSource = files;
        bool empty = dns.Count == 0 && endpoints.Count == 0 && files.Count == 0;
        ContextEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ContextLists.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Re-fills the Context tab for the shown second and the culprits grid's selection (the top row when
    /// nothing is selected), or shows the "pick something" state when there is nothing to show.</summary>
    private void RefreshContext()
    {
        var tick = _secondTick;
        int? identityId = SecondPanel.SelectedIdentityId;
        if (tick is null || identityId is null) { ClearContext("Select a second and a process."); return; }
        ShowContext(tick.UnixTime, identityId.Value);
    }

    private void ClearContext(string header)
    {
        ContextHeader.Text = header;
        DnsList.ItemsSource = null;
        EndpointList.ItemsSource = null;
        FileList.ItemsSource = null;
        ContextEmpty.Visibility = Visibility.Collapsed;
        ContextLists.Visibility = Visibility.Visible;
    }

    /// <summary>The tab currently in front. Lets a caller decide whether a timeline click may switch tabs.</summary>
    public AnalysisTab CurrentTab =>
        ReferenceEquals(Tabs.SelectedItem, TabSecond) ? AnalysisTab.Second
        : ReferenceEquals(Tabs.SelectedItem, TabOverview) ? AnalysisTab.Overview
        : ReferenceEquals(Tabs.SelectedItem, TabRange) ? AnalysisTab.Range
        : ReferenceEquals(Tabs.SelectedItem, TabAnomalies) ? AnalysisTab.Anomalies
        : ReferenceEquals(Tabs.SelectedItem, TabProcess) ? AnalysisTab.Process
        : AnalysisTab.Context;

    public void SelectTab(AnalysisTab tab)
    {
        var item = TabItemFor(tab);
        if (item.Visibility == Visibility.Visible) Tabs.SelectedItem = item;
    }

    /// <summary>Re-selects the row for the anomaly that was selected before the rows were rebuilt. The row objects
    /// are new and so is the <see cref="Anomaly"/> inside them, so the match goes through <see cref="AnomalyKey"/>;
    /// the list keeps no selection when that detection is gone or is filtered out. Does not raise
    /// <see cref="AnomalySelected"/>, so a refresh never re-zooms the timeline under the user, and scrolls the row
    /// back into view because the rebuilt list starts at the top.</summary>
    private void RestoreAnomalySelection(Anomaly? previous)
    {
        if (previous is null) return;
        AnomalyRow? row = null;
        foreach (var r in _anomalyRows) if (AnomalyKey.Equals(r.Anomaly, previous)) { row = r; break; }
        if (row is null || AnomalyList.Items.IndexOf(row) < 0) return;
        _suppressSelection = true;
        try
        {
            AnomalyList.SelectedItem = row;
            AnomalyList.ScrollIntoView(row);
        }
        finally { _suppressSelection = false; }
        ShowExplanation(row);
    }

    /// <summary>Selects the row for <paramref name="a"/>, un-filtering its metric if needed. Does not raise <see cref="AnomalySelected"/>.</summary>
    public void SelectAnomaly(Anomaly a)
    {
        var row = _anomalyRows.FirstOrDefault(x => ReferenceEquals(x.Anomaly, a))
            ?? _anomalyRows.FirstOrDefault(x => x.Anomaly == a)
            ?? _anomalyRows.FirstOrDefault(x => AnomalyKey.Equals(x.Anomaly, a));
        if (row is null) return;
        if (AnomalyList.Items.IndexOf(row) < 0)
        {
            _suppressFilter = true;
            try
            {
                OnlyHigh.IsChecked = false;
                foreach (var t in _metricToggles) if (t.Tag is Metric m && m == a.Metric) t.IsChecked = true;
            }
            finally { _suppressFilter = false; }
            ApplyAnomalyFilter();
        }
        _suppressSelection = true;
        try
        {
            AnomalyList.SelectedItem = row;
            AnomalyList.ScrollIntoView(row);
        }
        finally { _suppressSelection = false; }
        ShowExplanation(row);
    }

    /// <summary>Fills the Process tab for the identity and switches to it. Does not raise <see cref="ProcessSelected"/>.</summary>
    public void ShowProcess(int identityId)
    {
        if (_ctx is null) return;
        var pick = _picks.FirstOrDefault(p => p.IdentityId == identityId);
        _suppressSelection = true;
        try
        {
            if (pick is not null && !ProcessMatches.Items.Contains(pick))
            {
                ProcessSearch.Text = "";
                ApplyProcessFilter();
            }
            ProcessMatches.SelectedItem = pick;
        }
        finally { _suppressSelection = false; }
        FillProcessDetails(identityId);
        SelectTab(AnalysisTab.Process);
    }

    /// <summary>Shows the indeterminate progress bar in the Anomalies header while detection runs.</summary>
    public void SetBusy(bool busy)
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateAnomalyEmptyState();
    }

    /// <summary>Easy hides the Range and Process tabs, the Overview disk/network/score columns, the anomaly peak/baseline
    /// columns and the Explain expander, and shows only the top five processes.</summary>
    public void ApplyDetailLevel(DetailLevel level)
    {
        _detailLevel = level;
        bool pro = level == DetailLevel.Professional;
        var proOnly = pro ? Visibility.Visible : Visibility.Collapsed;
        TabRange.Visibility = proOnly;
        TabProcess.Visibility = proOnly;
        TabContext.Visibility = proOnly;
        OverviewColDisk.Visibility = proOnly;
        OverviewColNet.Visibility = proOnly;
        OverviewColScore.Visibility = proOnly;
        ContributorsExpander.Visibility = proOnly;

        var cols = AnomalyGridView.Columns;
        bool hasPeak = cols.Contains(ColPeak);
        if (pro && !hasPeak) { cols.Insert(4, ColPeak); cols.Insert(5, ColBaseline); }
        else if (!pro && hasPeak) { cols.Remove(ColPeak); cols.Remove(ColBaseline); }

        _rowLimit = pro ? TopN : 5;
        OverviewGrid.ItemsSource = _overviewRows.Take(_rowLimit).ToList();
        RangeGrid.ItemsSource = _rangeRows.Take(_rowLimit).ToList();

        if (Tabs.SelectedItem is TabItem selected && selected.Visibility != Visibility.Visible)
            Tabs.SelectedItem = TabOverview;
    }

    private TabItem TabItemFor(AnalysisTab tab) => tab switch
    {
        AnalysisTab.Second => TabSecond,
        AnalysisTab.Overview => TabOverview,
        AnalysisTab.Range => TabRange,
        AnalysisTab.Anomalies => TabAnomalies,
        AnalysisTab.Process => TabProcess,
        _ => TabContext,
    };

    private void UseView_Click(object sender, RoutedEventArgs e) => UseViewAsRangeRequested?.Invoke();

    private void OverviewGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenProcess(OverviewGrid.SelectedItem as ShareRow);

    private void RangeGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenProcess(RangeGrid.SelectedItem as ShareRow);

    private void OpenProcess(ShareRow? row)
    {
        if (row is null) return;
        ProcessSelected?.Invoke(row.IdentityId);
        ShowProcess(row.IdentityId);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFilter) return;
        ApplyAnomalyFilter();
    }

    private void AnomalyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = AnomalyList.SelectedItem as AnomalyRow;
        ShowExplanation(row);
        if (_suppressSelection || row is null) return;
        AnomalySelected?.Invoke(row.Anomaly);
    }

    private void ShowExplanation(AnomalyRow? row)
    {
        ContributorsGrid.ItemsSource = row?.Contributors;
        ContextText.Text = row?.Context ?? "";
    }

    private void ProcessSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyProcessFilter();

    private void ProcessMatches_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || ProcessMatches.SelectedItem is not ProcessPick pick) return;
        FillProcessDetails(pick.IdentityId);
        ProcessSelected?.Invoke(pick.IdentityId);
    }

    private void ClearOverlay_Click(object sender, RoutedEventArgs e)
    {
        _suppressSelection = true;
        try { ProcessMatches.SelectedItem = null; }
        finally { _suppressSelection = false; }
        ProcessSelected?.Invoke(null);
    }
}
