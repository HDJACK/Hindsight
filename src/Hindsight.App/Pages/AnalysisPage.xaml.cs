using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hindsight.App.Controls;
using Hindsight.App.Services;
using Hindsight.Core.Analysis;
using Hindsight.Core.Context;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Recordings;
using Hindsight.Core.Settings;
using Wpf.Ui.Controls;

namespace Hindsight.App.Pages;

public partial class AnalysisPage : Page
{
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromSeconds(10);

    private Recording? _recording;
    private IIdentitySource _source;
    private bool _live = true;
    private bool _refreshQueued;

    private readonly AnalysisRunner _runner = new();
    private IReadOnlyList<Tick> _ticks = Array.Empty<Tick>();
    private IReadOnlyList<Marker> _markers = Array.Empty<Marker>();
    private int _cores = Environment.ProcessorCount;
    private AppSettings _settings;
    private DateTime _lastAnalysis = DateTime.MinValue;
    private int _sourceGeneration;
    private int? _overlayId;
    private CancellationTokenSource? _compareCts;

    public AnalysisPage()
    {
        InitializeComponent();
        AppHost.Analysis = this;
        _source = AppHost.Services.LiveIdentities;
        _settings = AppHost.Settings.Current;
        Timeline.MarkerDeleteRequested += m => { if (_live && AppHost.Services.IsRunning) AppHost.Services.Markers.Remove(m); };
        Timeline.SelectionChanged += t =>
        {
            Tabs.ShowSecond(t, CurrentIds);
            // Do not yank the user out of the Anomalies, Range or Process tab.
            if (Tabs.CurrentTab is AnalysisTab.Second or AnalysisTab.Overview) Tabs.SelectTab(AnalysisTab.Second);
        };
        Timeline.RangeChanged += r => Tabs.SetRange(r);
        Timeline.AnomalyClicked += a => { Tabs.SelectTab(AnalysisTab.Anomalies); Tabs.SelectAnomaly(a); };
        Tabs.UseViewAsRangeRequested += () => Timeline.SelectViewAsRange();
        Tabs.AnomalySelected += a =>
        {
            Timeline.ZoomTo(a.Range.FromUnix, a.Range.ToUnix);
            Timeline.SetRange(a.Range);
            Timeline.HighlightedAnomaly = a;
        };
        Tabs.ProcessSelected += id => { _overlayId = id; UpdateOverlay(); };
        Toolbar.Target = Timeline;
        Lanes.SelectionChanged += kinds =>
        {
            AppHost.Settings.Save(AppHost.Settings.Current with { VisibleLanes = AppSettings.LanesToString(kinds) });
            Timeline.SetLanes(kinds);
        };
        AppHost.Services.TickWritten += _ => { if (_live) QueueRefresh(); };
        AppHost.Settings.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            bool detection = DetectionSettings.Changed(_settings, s);
            _settings = s;
            ApplyDetailLevel();
            // A changed detection threshold makes the current anomalies stale.
            if (!detection) return;
            _lastAnalysis = DateTime.MinValue;
            if (_live) MaybeAnalyze(); else if (_recording is not null) RunAnalysis();
        });
        AppHost.DetailLevelChanged += () => Dispatcher.BeginInvoke(ApplyDetailLevel);
        Loaded += (_, _) => { ConsumePending(); if (_live && _recording is null) ShowLive(); ApplyDetailLevel(); };
    }

    public void ConsumePending()
    {
        var p = AppHost.PendingAnalysis;
        AppHost.PendingAnalysis = null;
        p?.Invoke(this);
    }

    /// <summary>Applies the detail level to the lane strip, the context strip and the header. LaneStrip.SetSelection
    /// suppresses its own SelectionChanged, so calling this on every Settings.Changed does not re-save.</summary>
    private void ApplyDetailLevel()
    {
        bool pro = AppHost.DetailLevel == DetailLevel.Professional;
        Lanes.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        var s = AppHost.Settings.Current;
        if (pro)
        {
            var kinds = s.VisibleLaneKinds();
            Lanes.SetSelection(kinds);
            Timeline.SetLanes(kinds);
        }
        else
        {
            Timeline.SetLanes(new[] { LaneKind.Cpu, LaneKind.Gpu, LaneKind.FileIo, LaneKind.RamFree });
        }
        Timeline.SetContext(pro && s.ShowForegroundApp, pid => CurrentIds.ByPid(pid)?.DisplayName);
        Timeline.ShowMiniMap = pro;
        Tabs.ApplyDetailLevel(AppHost.DetailLevel);
        Compare.ApplyDetailLevel(AppHost.DetailLevel);
        UpdateMeta();
    }

    private void UpdateMeta()
    {
        bool pro = AppHost.DetailLevel == DetailLevel.Professional;
        if (_live)
        {
            var s = AppHost.Services.Current;
            MetaText.Text = pro
                ? $"Last {s.BufferMinutes} min · {s.SampleIntervalSeconds} s interval · updates every tick"
                : $"Last {s.BufferMinutes} min";
        }
        else if (_recording is not null)
        {
            var r = _recording;
            MetaText.Text = pro
                ? $"{DateTimeOffset.FromUnixTimeSeconds(r.Meta.StartUnix).ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Format.Duration(r.Meta.DurationSeconds)} · {r.Meta.IntervalSeconds} s interval · {r.Meta.CoreCount} cores · {r.Meta.MachineName}"
                    + (string.IsNullOrWhiteSpace(r.Meta.Notes) ? "" : " · " + r.Meta.Notes)
                : Format.Duration(r.Meta.DurationSeconds);
        }
    }

    /// <summary>The identity source for the shown data: the live one while following the buffer, the recording otherwise.</summary>
    private IIdentitySource CurrentIds => _live ? AppHost.Services.LiveIdentities : _source;

    /// <summary>The context source for the shown data: the live store while following the buffer, the opened
    /// recording otherwise (null while comparing, where there is no single source).</summary>
    private IContextSource? CurrentContext => _live ? AppHost.Services.Context : _recording;

    public void ShowLive()
    {
        ResetSource();
        _live = true; _recording = null; _source = AppHost.Services.LiveIdentities;
        Toolbar.ShowFollow = true;
        Timeline.SetFollow(true);
        SourceTitle.Text = "Live buffer";
        UpdateMeta();
        _cores = Environment.ProcessorCount;
        Refresh();
        Tabs.SetSource(new AnalysisContext(_ticks, CurrentIds, _cores, _markers, Array.Empty<Anomaly>(), CurrentContext));
        // Same as ShowRecording: the second shown for the previous source is meaningless here.
        Tabs.ShowSecond(null, _source);
    }

    public void ShowRecording(string path)
    {
        try
        {
            var r = RecordingReader.Open(path);
            ResetSource();
            _live = false; _recording = r; _source = r;
            Toolbar.ShowFollow = false;
            SourceTitle.Text = r.Meta.Name;
            UpdateMeta();
            Timeline.SetWindow(0);
            Timeline.AllowMarkerDeletion = false;
            Timeline.SetData(r.Ticks, r.Markers);
            Timeline.Fit();
            _ticks = r.Ticks; _markers = r.Markers; _cores = r.Meta.CoreCount;
            Tabs.SetSource(new AnalysisContext(_ticks, CurrentIds, _cores, _markers, Array.Empty<Anomaly>(), CurrentContext));
            Tabs.ShowSecond(null, _source);
            Info.IsOpen = false;
            RunAnalysis();
        }
        catch (Exception ex)
        {
            Info.Title = "Could not open recording"; Info.Message = ex.Message; Info.Severity = InfoBarSeverity.Error; Info.IsOpen = true;
        }
    }

    /// <summary>Loads both recordings and compares them off the UI thread, then shows the compare view in place of
    /// the normal timeline and tabs. A newer source (Live, Open, another Compare) supersedes a run still in flight.</summary>
    public async void ShowCompare(string pathA, string pathB)
    {
        ResetSource();                       // also cancels a compare still running and restores the normal view
        _live = false; _recording = null;
        Toolbar.ShowFollow = false;
        AnalysisGrid.Visibility = Visibility.Collapsed;
        Compare.Visibility = Visibility.Visible;
        Compare.SetBusy(true);
        Info.IsOpen = false;
        SourceTitle.Text = "Compare";
        MetaText.Text = "Loading…";

        int generation = _sourceGeneration;
        var settings = AppHost.Settings.Current;
        var cts = new CancellationTokenSource();
        _compareCts = cts;
        try
        {
            var token = cts.Token;
            var (ra, rb, result) = await Task.Run(() =>
            {
                var ra = RecordingReader.Open(pathA);
                var rb = RecordingReader.Open(pathB);
                return (ra, rb, RecordingComparer.Compare(ra, rb, settings, token));
            }, token);
            if (generation != _sourceGeneration) return;   // superseded while we awaited
            _source = ra;
            SourceTitle.Text = $"Compare: {ra.Meta.Name} vs {rb.Meta.Name}";
            MetaText.Text = $"A {Format.Time(ra.Meta.StartUnix)} · {Format.Duration(ra.Meta.DurationSeconds)}"
                          + $"  ·  B {Format.Time(rb.Meta.StartUnix)} · {Format.Duration(rb.Meta.DurationSeconds)}";
            Compare.Show(ra, rb, result);
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            Log.Error("Compare", ex);
            if (generation != _sourceGeneration) return;
            // Leave nothing but the error on screen: an empty compare pane is worse than the normal (empty) view.
            Compare.Visibility = Visibility.Collapsed;
            AnalysisGrid.Visibility = Visibility.Visible;
            SourceTitle.Text = "Compare";
            MetaText.Text = "";
            Info.Title = "Could not compare the recordings"; Info.Message = ex.Message; Info.Severity = InfoBarSeverity.Error; Info.IsOpen = true;
        }
        finally
        {
            if (ReferenceEquals(_compareCts, cts)) _compareCts = null;
            cts.Dispose();
            if (generation == _sourceGeneration) Compare.SetBusy(false);
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; if (IsVisible && _live) Refresh(); });
    }

    private void Refresh()
    {
        if (!_live || !AppHost.Services.IsRunning) return;
        var services = AppHost.Services;
        var ticks = services.Ring.ReadAll();
        var markers = ticks.Count == 0 ? Array.Empty<Marker>() : services.Markers.InRange(ticks[0].UnixTime, ticks[^1].UnixTime);
        _ticks = ticks; _markers = markers; _cores = Environment.ProcessorCount;
        Timeline.SetWindow(services.Current.RingCapacity);
        Timeline.AllowMarkerDeletion = true;
        Timeline.SetData(ticks, markers);
        // Live only: the ring just handed us a new tick list, so the overlay has to be re-extracted against it.
        UpdateOverlay();
        MaybeAnalyze();
    }

    /// <summary>Drops everything derived from the previous source, stops a detection or comparison run still in
    /// flight and puts the normal timeline/tabs view back in front.</summary>
    private void ResetSource()
    {
        _sourceGeneration++;
        _runner.Cancel();
        var compare = _compareCts;
        _compareCts = null;
        if (compare is not null)
        {
            try { compare.Cancel(); }
            catch (ObjectDisposedException) { /* the run finished first */ }
        }
        Compare.SetBusy(false);
        Compare.Visibility = Visibility.Collapsed;
        AnalysisGrid.Visibility = Visibility.Visible;
        Tabs.SetBusy(false);
        _lastAnalysis = DateTime.MinValue;
        _ticks = Array.Empty<Tick>();
        _markers = Array.Empty<Marker>();
        // Clear the drawn data too: Refresh() returns early when the pipeline is not running, so without this the
        // previous recording's ticks stay on the timeline under the new source's title.
        Timeline.SetData(Array.Empty<Tick>(), Array.Empty<Marker>());
        _overlayId = null;
        Timeline.SetOverlay(null);
        Timeline.HighlightedAnomaly = null;
        Timeline.SetAnomalies(Array.Empty<Anomaly>());
        Timeline.SetRange(null);
        Tabs.SetRange(null);
    }

    /// <summary>Re-extracts the selected process against the current ticks; the timeline draws the overlay
    /// index-aligned to them, so it must be rebuilt whenever the tick list is replaced.</summary>
    private void UpdateOverlay() =>
        Timeline.SetOverlay(_overlayId is null ? null : ProcessSeries.Extract(_ticks, CurrentIds, _overlayId.Value));

    /// <summary>Live only: re-runs detection at most every ten seconds, and only while the page is on screen.</summary>
    private void MaybeAnalyze()
    {
        if (!_live || !IsVisible || _ticks.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now - _lastAnalysis < AnalysisInterval) return;
        _lastAnalysis = now;
        RunAnalysis();
    }

    private async void RunAnalysis()
    {
        var ticks = _ticks; var ids = CurrentIds; var markers = _markers; int cores = _cores; var context = CurrentContext;
        if (ticks.Count == 0) return;
        int generation = _sourceGeneration;
        Tabs.SetBusy(true);
        try
        {
            var source = await _runner.AnalyzeAsync(ticks, ids, cores, markers, AppHost.Settings.Current, context);
            if (source is null || generation != _sourceGeneration) return; // superseded by a newer run, or the source changed while we awaited
            Tabs.SetSource(source);
            Timeline.SetAnomalies(source.Context.Anomalies);
        }
        catch (Exception) { /* AnalysisRunner already logged the failure. */ }
        finally { if (!_runner.IsRunning) Tabs.SetBusy(false); }
    }

    private void Live_Click(object sender, RoutedEventArgs e) => ShowLive();

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Hindsight recordings|*.hsrec", InitialDirectory = AppHost.Services.Recordings.Folder };
        if (dlg.ShowDialog() == true) ShowRecording(dlg.FileName);
    }
}
