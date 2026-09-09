using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Hindsight.App.Controls;
using Hindsight.App.Services;
using Hindsight.Core.Analysis;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;
using Hindsight.Core.Settings;
using Hindsight.Core.Storage;
using Wpf.Ui.Controls;

namespace Hindsight.App.Pages;

public partial class LivePage : Page
{
    private static readonly TimeSpan AnalysisInterval = TimeSpan.FromSeconds(10);

    private bool _refreshQueued;
    private MarkerStore? _markerStore;
    private readonly Action<Marker> _onMarker;
    private string? _lastSavedPath;

    private readonly AnalysisRunner _runner = new();
    private IReadOnlyList<Tick> _ticks = Array.Empty<Tick>();
    private IReadOnlyList<Marker> _markers = Array.Empty<Marker>();
    private IIdentitySource _ids;
    private AppSettings _settings;
    private DateTime _lastAnalysis = DateTime.MinValue;
    private int _sourceGeneration;
    private int? _overlayId;

    public LivePage()
    {
        InitializeComponent();
        AppHost.Live = this;
        _ids = AppHost.Services.LiveIdentities;
        _settings = AppHost.Settings.Current;
        _onMarker = _ => QueueRefresh();
        Timeline.SelectionChanged += t =>
        {
            Tabs.ShowSecond(AppHost.Services.IsRunning ? t : null, AppHost.Services.LiveIdentities);
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
        Timeline.AllowMarkerDeletion = true;
        Timeline.MarkerDeleteRequested += m => { if (AppHost.Services.IsRunning) AppHost.Services.Markers.Remove(m); };
        Toolbar.Target = Timeline;
        Toolbar.ShowFollow = true;
        Lanes.SelectionChanged += kinds =>
        {
            AppHost.Settings.Save(AppHost.Settings.Current with { VisibleLanes = AppSettings.LanesToString(kinds) });
            Timeline.SetLanes(kinds);
        };
        AppHost.Services.TickWritten += _ => QueueRefresh();
        HookMarkers();
        AppHost.Services.PipelineRestarted += () => Dispatcher.BeginInvoke(() =>
        {
            // The marker store was rebuilt; subscribe to the new one, and treat this as a new source:
            // cancel any in-flight detection and reset the throttle so stale results are not applied.
            _sourceGeneration++;
            _runner.Cancel();
            _lastAnalysis = DateTime.MinValue;
            // Nothing derived from the old ring may survive into the new one.
            _overlayId = null;
            Timeline.SetOverlay(null);
            Timeline.HighlightedAnomaly = null;
            Timeline.SetAnomalies(Array.Empty<Anomaly>());
            Timeline.SetRange(null);
            Tabs.SetRange(null);
            HookMarkers();
            Refresh();
            UpdateRecordingUi();
        });
        AppHost.Services.Recordings.Changed += () => Dispatcher.BeginInvoke(UpdateRecordingUi);
        AppHost.Settings.Changed += s => Dispatcher.BeginInvoke(() =>
        {
            // A changed detection threshold makes the current anomalies stale; skip the throttle once.
            if (DetectionSettings.Changed(_settings, s)) _lastAnalysis = DateTime.MinValue;
            _settings = s;
            ApplyDetailLevel(); Refresh(); UpdateRecordingUi();
        });
        AppHost.DetailLevelChanged += () => Dispatcher.BeginInvoke(ApplyDetailLevel);
        Loaded += (_, _) => { ApplyDetailLevel(); Refresh(); UpdateRecordingUi(); };
    }

    public void FocusRecordingName() => RecordingName.Focus();

    /// <summary>Applies the detail level to the cards, the lane strip and the context strip. LaneStrip.SetSelection
    /// suppresses its own SelectionChanged, so calling this on every Settings.Changed does not re-save.</summary>
    private void ApplyDetailLevel()
    {
        bool pro = AppHost.DetailLevel == DetailLevel.Professional;
        SecondaryCards.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
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
        Timeline.SetContext(pro && s.ShowForegroundApp, pid => AppHost.Services.LiveIdentities.ByPid(pid)?.DisplayName);
        Timeline.ShowMiniMap = pro;
        Tabs.ApplyDetailLevel(AppHost.DetailLevel);
    }

    private void UpdateRecordingUi()
    {
        var r = AppHost.Services.Recordings;
        RecordButton.Content = r.IsRecording ? "Stop recording" : "Start recording";
        RecordButton.Icon = new SymbolIcon(r.IsRecording ? SymbolRegular.Stop24 : SymbolRegular.Play24);
        RecordButton.Appearance = r.IsRecording ? ControlAppearance.Danger : ControlAppearance.Primary;
        RecordingName.IsEnabled = !r.IsRecording;
        SaveBufferButton.Content = $"Save last {AppHost.Services.Current.BufferMinutes} min";
    }

    private void HookMarkers()
    {
        var store = AppHost.Services.Markers;
        if (ReferenceEquals(store, _markerStore)) return;
        if (_markerStore is not null) { _markerStore.Added -= _onMarker; _markerStore.Removed -= _onMarker; }
        _markerStore = store;
        _markerStore.Added += _onMarker;
        _markerStore.Removed += _onMarker;
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _refreshQueued = false; if (IsVisible) Refresh(); });
    }

    private void Refresh()
    {
        if (!AppHost.Services.IsRunning) return;
        var services = AppHost.Services;
        var ticks = services.Ring.ReadAll();
        var markers = ticks.Count == 0 ? Array.Empty<Marker>() : services.Markers.InRange(ticks[0].UnixTime, ticks[^1].UnixTime);
        _ticks = ticks; _markers = markers; _ids = services.LiveIdentities;
        Timeline.SetWindow(services.Current.RingCapacity);
        Timeline.SetData(ticks, markers);
        // The ring just handed us a new tick list, so the overlay has to be re-extracted against it or it goes stale.
        UpdateOverlay();
        var s = services.Current;
        BufferText.Text = $"Buffer: last {s.BufferMinutes} min · {s.SampleIntervalSeconds} s interval";
        if (ticks.Count == 0) return;
        MaybeAnalyze();
        var last = ticks[^1];
        if (last.Has(TickFlags.Gap)) return;
        int iv = last.IntervalSeconds;
        CpuCard.Update(Format.Percent(last.CpuTotalPercent), last.CpuTotalPercent);
        GpuCard.Update(Format.Percent(last.GpuTotalPercent), last.GpuTotalPercent);
        IoCard.Update(Format.Rate(last.DiskBytes, iv), last.DiskBytes / (double)iv);
        RamCard.Update(Format.Bytes(last.RamAvailableBytes), last.RamAvailableBytes);

        NetCard.Update(Format.Rate(last.NetBytes, iv), last.NetBytes / (double)iv);
        CpuMhzCard.Update($"{last.CpuMhz} MHz", last.CpuMhz);
        DiskLatencyCard.Update($"{last.DiskLatencyMs:0.0} ms", last.DiskLatencyMs);
        bool onAc = last.Has(TickFlags.OnAc);
        string battery = onAc && last.BatteryPercent >= 0 ? $"AC · {last.BatteryPercent} %"
            : onAc ? "AC"
            : last.BatteryPercent >= 0 ? $"{last.BatteryPercent} % battery"
            : "—";
        CommitCard.Update($"{Format.Percent(last.CommitPercent)} · {battery}", last.CommitPercent);
    }

    /// <summary>Re-extracts the selected process against the current ticks; the timeline draws the overlay
    /// index-aligned to them, so it must be rebuilt whenever the tick list is replaced.</summary>
    private void UpdateOverlay() =>
        Timeline.SetOverlay(_overlayId is null ? null : ProcessSeries.Extract(_ticks, _ids, _overlayId.Value));

    /// <summary>Re-runs detection at most every ten seconds, and only while the page is on screen.</summary>
    private void MaybeAnalyze()
    {
        if (!IsVisible || _ticks.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now - _lastAnalysis < AnalysisInterval) return;
        _lastAnalysis = now;
        RunAnalysis();
    }

    private async void RunAnalysis()
    {
        var ticks = _ticks; var ids = _ids; var markers = _markers;
        if (ticks.Count == 0) return;
        int generation = _sourceGeneration;
        Tabs.SetBusy(true);
        try
        {
            var source = await _runner.AnalyzeAsync(ticks, ids, Environment.ProcessorCount, markers, AppHost.Settings.Current, AppHost.Services.Context);
            if (source is null || generation != _sourceGeneration) return; // superseded by a newer run, or the source changed while we awaited
            Tabs.SetSource(source);
            Timeline.SetAnomalies(source.Context.Anomalies);
        }
        catch (Exception) { /* AnalysisRunner already logged the failure. */ }
        finally { if (!_runner.IsRunning) Tabs.SetBusy(false); }
    }

    private void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        string text = string.IsNullOrWhiteSpace(MarkerText.Text) ? "Manual" : MarkerText.Text.Trim();
        // Place the marker on the selected second when one is selected, otherwise on "now".
        long at = Timeline.SelectedUnixTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AppHost.Services.AddMarkerAt(at, MarkerKind.Manual, text);
        MarkerText.Text = "";
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        var r = AppHost.Services.Recordings;
        try
        {
            if (r.IsRecording)
            {
                string path = r.Stop();
                ShowSaved(path);
            }
            else
            {
                r.Start(RecordingName.Text, "");
                RecordingName.Text = "";
                RecInfo.IsOpen = false;
            }
        }
        catch (Exception ex) { ShowRecInfo("Recording failed", ex.Message, InfoBarSeverity.Error); }
    }

    private void SaveBuffer_Click(object sender, RoutedEventArgs e)
    {
        try { ShowSaved(AppHost.Services.Recordings.SaveBuffer(RecordingName.Text, "")); RecordingName.Text = ""; }
        catch (Exception ex) { ShowRecInfo("Snapshot failed", ex.Message, InfoBarSeverity.Error); }
    }

    private void ShowSaved(string path)
    {
        _lastSavedPath = path;
        ShowRecInfo("Recording saved", System.IO.Path.GetFileName(path), InfoBarSeverity.Success);
        OpenSavedButton.Visibility = Visibility.Visible;
    }

    private void ShowRecInfo(string title, string text, InfoBarSeverity severity)
    {
        OpenSavedButton.Visibility = Visibility.Collapsed;
        RecInfo.Title = title; RecInfo.Message = text; RecInfo.Severity = severity; RecInfo.IsOpen = true;
    }

    private void OpenSaved_Click(object sender, RoutedEventArgs e)
    {
        if (_lastSavedPath is not null) AppHost.Window?.OpenRecording(_lastSavedPath);
    }
}
