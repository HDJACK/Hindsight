using System.IO;
using System.Windows;
using Hindsight.App.Services;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using Hindsight.Core.Win32;
using Wpf.Ui.Controls;

namespace Hindsight.App;

public partial class App : System.Windows.Application
{
    private AppServices? _services;
    private MainWindow? _window;
    private TrayService? _tray;
    private HotkeyService? _hotkey;
    private SystemEventService? _sysEvents;
    private EventLogMarkerService? _eventLogMarkers;
    private readonly List<(string title, string text, InfoBarSeverity severity)> _pending = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var store = new SettingsStore(Path.Combine(AppSettings.DefaultDataFolder, "settings.json"));
        var load = store.Load();
        AppHost.Settings = store;

        try
        {
            _services = new AppServices(store);
            _services.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup", ex);
            System.Windows.MessageBox.Show("Hindsight could not start:\n" + ex.Message, "Hindsight",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        AppHost.Services = _services;
        SessionEnding += (_, _) => _services?.Recordings.StopIfRecording();

        _window = new MainWindow();
        AppHost.Window = _window;
        _window.ApplyBounds(store.Current.WindowBounds);
        AppHost.Theme = new ThemeService(_window);
        _window.SourceInitialized += (_, _) => AppHost.Theme.Apply(store.Current.Theme);
        _window.Loaded += (_, _) => FlushPending();
        _services.TickWritten += _window.OnTick;
        _services.PipelineRestarted += () => _window.Dispatcher.BeginInvoke(_window.UpdateStatus);

        bool showRequested = e.Args.Any(a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
        // WPF-UI's tray needs a PresentationSource; create it by showing the window once (invisibly unless requested).
        MainWindow = _window;
        if (!showRequested) { _window.Opacity = 0; _window.ShowInTaskbar = false; _window.ShowActivated = false; }
        _window.Show();
        if (!showRequested) { _window.Hide(); _window.Opacity = 1; _window.ShowInTaskbar = true; _window.ShowActivated = true; }

        _services.Recordings.Changed += () => _window?.Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetRecording(_services.Recordings.IsRecording);
            UpdateTrayTooltip();
            _window?.UpdateRecordingIndicator();
        });
        _services.RecordingAutoSaved += path => Queue("Recording saved",
            "The pipeline restarted, so the running recording was saved as " + Path.GetFileName(path) + ".", InfoBarSeverity.Informational);
        // Subscribed before the services that add notes from their constructors (system events, event log),
        // so a startup note is not silently missed by the status bar and the tray tooltip.
        _services.ContextNotesChanged += () => _window?.Dispatcher.BeginInvoke(() =>
        {
            _window?.UpdateStatus();
            UpdateTrayTooltip();
        });

        _tray = new TrayService(ShowWindow, () => _services.AddMarker(MarkerKind.Manual, "Manual"), OpenLiveForRecording, StopRecordingFromTray, ExitApp);
        if (!_tray.Register())
            Queue("Tray icon unavailable", "The tray icon could not be registered. Use --show to open the window.", InfoBarSeverity.Warning);
        UpdateTrayTooltip();

        HotkeyGesture.TryParse(store.Current.MarkerHotkey, out var gesture);
        _hotkey = new HotkeyService(gesture ?? new HotkeyGesture(HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x4D),
            () => _services.AddMarker(MarkerKind.Manual, "Manual"));
        if (!_hotkey.Registered)
            Queue("Hotkey not registered", $"{_hotkey.Gesture} is already taken by another application. Change it in Settings.", InfoBarSeverity.Warning);
        _sysEvents = new SystemEventService(_services);
        _eventLogMarkers = new EventLogMarkerService(m => _services.AddMarkerAt(m.UnixTime, m.Kind, m.Text));
        foreach (var note in _eventLogMarkers.Notes) _services.AddContextNote(note);
        // The handler above only fires for notes added after it was attached; refresh once here so the notes
        // collected during startup are on screen even if every one of them was a duplicate.
        _window.UpdateStatus();
        UpdateTrayTooltip();

        if (load.WasReset) Queue("Settings were reset", string.Join(" ", load.Notes), InfoBarSeverity.Warning);
        else if (load.Notes.Count > 0) Queue("Some settings were adjusted", string.Join(" ", load.Notes), InfoBarSeverity.Informational);
        if (!_services.Recorder.EtwActive)
            Queue("ETW unavailable", (_services.Recorder.DegradedReason ?? "unknown reason") + ". File I/O and network are not recorded.", InfoBarSeverity.Warning);

        if (showRequested)
            ShowWindow();
        Log.Info($"Startup complete (pid {Environment.ProcessId}, args: {string.Join(' ', e.Args)})");

        // --exit-after <seconds> shuts down through the same path as the tray's Exit item.
        int exitIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--exit-after", StringComparison.OrdinalIgnoreCase));
        if (exitIdx >= 0 && exitIdx + 1 < e.Args.Length && int.TryParse(e.Args[exitIdx + 1], out int seconds))
        {
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) => { timer.Stop(); ExitApp(); };
            timer.Start();
        }
    }

    public HotkeyService? Hotkey => _hotkey;

    /// <summary>Applies everything that can change without a restart, restarts the pipeline if needed, and returns problems.</summary>
    public IReadOnlyList<string> ApplyRuntimeSettings(AppSettings old, AppSettings now)
    {
        var problems = new List<string>();
        if (_services is null) return problems;

        if (Pages.SettingsPage.NeedsRestart(old, now))
        {
            try { _services.RestartPipeline(); }
            catch (Exception ex)
            {
                problems.Add("Pipeline restart failed: " + ex.Message + " Previous settings restored.");
                try { AppHost.Settings.Save(old); _services.RestartPipeline(); }
                catch (Exception ex2) { problems.Add("Recording is stopped: " + ex2.Message + " Fix the data folder and apply again."); }
            }
            UpdateTrayTooltip();
            _window?.UpdateStatus();
        }
        if (old.Theme != now.Theme) AppHost.Theme.Apply(now.Theme);
        if (old.DetailLevel != now.DetailLevel) AppHost.RaiseDetailLevelChanged();
        if (old.MarkerHotkey != now.MarkerHotkey && _hotkey is not null && HotkeyGesture.TryParse(now.MarkerHotkey, out var g))
        {
            if (_hotkey.Rebind(g!)) _window?.ClearMessage("Hotkey not registered");
            else
            {
                problems.Add($"Hotkey {g} could not be registered (already in use).");
                _window?.ShowMessage("Hotkey not registered", $"{g} is already taken by another application. Change it in Settings.", InfoBarSeverity.Warning);
            }
        }
        if (old.StartWithWindows != now.StartWithWindows)
        {
            if (now.StartWithWindows && string.IsNullOrEmpty(Environment.ProcessPath))
            {
                problems.Add("Start with Windows: the executable path could not be determined; the task was not created.");
            }
            else
            {
                var (ok, output) = now.StartWithWindows
                    ? AutostartTask.Enable(Environment.ProcessPath ?? "")
                    : AutostartTask.Disable();
                if (!ok) problems.Add("Task Scheduler: " + output);
            }
        }
        return problems;
    }

    public void UpdateTrayTooltip()
    {
        if (_tray is null || _services is null) return;
        string text;
        if (!_services.IsRunning)
            text = "Hindsight – recording stopped";
        else
            text = _services.Recorder.EtwActive ? "Hindsight – recording" : "Hindsight – recording (ETW unavailable)";
        if (_services.Recordings.IsRecording)
            text += " · REC " + _services.Recordings.CurrentName;
        var counters = _services.MissingCounters;
        var notes = _services.ContextNoteSnapshot;
        if (counters.Count > 0)
            text += counters.Any(m => m.Contains("GPU")) ? " · GPU counters unavailable" : " · Some counters unavailable";
        if (notes.Count > 0)
            text += notes.Any(n => n.Contains("File paths disabled")) ? " · File paths disabled" : " · Some context unavailable";
        _tray.TooltipText = text;
    }

    /// <summary>Tray "Start recording…": open the window on the Recordings page, where Start/Stop live.</summary>
    private void OpenLiveForRecording()
    {
        ShowWindow();
        _window?.NavigateTo(typeof(Pages.RecordingsPage));
    }

    /// <summary>Tray "Stop recording": save, then show the result on the Analysis page.</summary>
    private void StopRecordingFromTray()
    {
        if (_services is null || !_services.Recordings.IsRecording) return;
        try
        {
            var path = _services.Recordings.Stop();
            Queue("Recording saved", Path.GetFileName(path), InfoBarSeverity.Success);
            ShowWindow();
            _window?.OpenRecording(path);
        }
        catch (Exception ex) { Queue("Recording could not be saved", ex.Message, InfoBarSeverity.Error); }
    }

    private void Queue(string title, string text, InfoBarSeverity severity)
    {
        _pending.Add((title, text, severity));
        if (_window?.IsLoaded == true) FlushPending();
    }

    private void FlushPending()
    {
        if (_window is null) return;
        foreach (var m in _pending) _window.ShowMessage(m.title, m.text, m.severity);
        _pending.Clear();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ExitApp()
    {
        Log.Info("ExitApp: begin");
        try
        {
            if (_services?.Recordings.IsRecording == true)
                Log.Info("ExitApp: a running recording will be auto-saved on shutdown");
            if (_window is not null) { _window.AllowClose = true; _window.Close(); }
            Log.Info("ExitApp: window closed");
            Shutdown();
            Log.Info("ExitApp: Shutdown() returned");
        }
        catch (Exception ex) { Log.Error("ExitApp", ex); throw; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("OnExit: begin");
        try
        {
            _hotkey?.Dispose(); Log.Info("OnExit: hotkey disposed");
            _sysEvents?.Dispose(); Log.Info("OnExit: system events disposed");
            _eventLogMarkers?.Dispose(); Log.Info("OnExit: event log markers disposed");
            _tray?.Unregister(); Log.Info("OnExit: tray unregistered");
            _services?.Dispose(); Log.Info("OnExit: services disposed");
        }
        catch (Exception ex) { Log.Error("OnExit", ex); }
        base.OnExit(e);
        Log.Info("OnExit: done, foreground threads=" + System.Diagnostics.Process.GetCurrentProcess().Threads.Count);
    }
}
