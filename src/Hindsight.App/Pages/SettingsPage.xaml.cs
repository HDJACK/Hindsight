using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using Hindsight.Core.Win32;
using Wpf.Ui.Controls;

namespace Hindsight.App.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFrom(AppHost.Settings.Current);
        AppHost.DetailLevelChanged += () => Dispatcher.BeginInvoke(() =>
        {
            Level.SelectedIndex = (int)AppHost.Settings.Current.DetailLevel;
            ApplyLevelVisibility(AppHost.Settings.Current.DetailLevel);
            // The rest of the form is left alone so unsaved edits survive.
        });
    }

    private void LoadFrom(AppSettings s)
    {
        BufferMinutes.Value = s.BufferMinutes;
        Interval.Value = s.SampleIntervalSeconds;
        DataFolder.Text = s.DataFolder;
        CpuSpike.Value = s.CpuSpikePercent;
        IoSpike.Value = s.FileIoSpikeMBps;
        NetSpike.Value = s.NetworkSpikeMBps;
        Baseline.Value = s.BaselineWindowSeconds;
        GpuSpike.Value = s.GpuSpikePercent;
        LatencySpike.Value = s.DiskLatencySpikeMs;
        CommitSpike.Value = s.CommitSpikePercent;
        Sensitivity.SelectedIndex = (int)s.AnomalySensitivity;
        MinAnomaly.Value = s.MinAnomalySeconds;
        WCpu.Value = s.WeightCpu; WIo.Value = s.WeightIo; WNet.Value = s.WeightNet; WMem.Value = s.WeightMemory; WGpu.Value = s.WeightGpu;
        Hotkey.Text = s.MarkerHotkey;
        Autostart.IsChecked = s.StartWithWindows;
        Theme.SelectedIndex = (int)s.Theme;
        StoreCmd.IsChecked = s.StoreCommandLines;
        CapturePaths.IsChecked = s.CaptureFilePaths;
        CaptureDns.IsChecked = s.CaptureDnsAndEndpoints;
        IdleAfter.Value = s.IdleAfterSeconds;
        ShowForeground.IsChecked = s.ShowForegroundApp;
        Level.SelectedIndex = (int)s.DetailLevel;
        ApplyLevelVisibility(s.DetailLevel);
    }

    private AppSettings ReadInto(AppSettings baseline) => baseline with
    {
        BufferMinutes = (int)(BufferMinutes.Value ?? 0),
        SampleIntervalSeconds = (int)(Interval.Value ?? 0),
        DataFolder = DataFolder.Text.Trim(),
        CpuSpikePercent = CpuSpike.Value ?? 0,
        FileIoSpikeMBps = IoSpike.Value ?? 0,
        NetworkSpikeMBps = NetSpike.Value ?? 0,
        BaselineWindowSeconds = (int)(Baseline.Value ?? 0),
        GpuSpikePercent = GpuSpike.Value ?? 0,
        DiskLatencySpikeMs = LatencySpike.Value ?? 0,
        CommitSpikePercent = CommitSpike.Value ?? 0,
        AnomalySensitivity = (AnomalySensitivity)Math.Max(0, Sensitivity.SelectedIndex),
        MinAnomalySeconds = (int)(MinAnomaly.Value ?? 0),
        WeightCpu = WCpu.Value ?? 0, WeightIo = WIo.Value ?? 0, WeightNet = WNet.Value ?? 0, WeightMemory = WMem.Value ?? 0, WeightGpu = WGpu.Value ?? 0,
        MarkerHotkey = Hotkey.Text.Trim(),
        StartWithWindows = Autostart.IsChecked == true,
        Theme = (AppTheme)Math.Max(0, Theme.SelectedIndex),
        StoreCommandLines = StoreCmd.IsChecked == true,
        CaptureFilePaths = CapturePaths.IsChecked == true,
        CaptureDnsAndEndpoints = CaptureDns.IsChecked == true,
        IdleAfterSeconds = (int)(IdleAfter.Value ?? 0),
        ShowForegroundApp = ShowForeground.IsChecked == true,
        DetailLevel = (DetailLevel)Math.Max(0, Level.SelectedIndex),
    };

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var old = AppHost.Settings.Current;
        var now = ReadInto(old);
        var errors = SettingsValidator.Validate(now);
        if (errors.Count > 0)
        {
            Show("Please fix these values", string.Join("\n", errors), InfoBarSeverity.Error);
            return;
        }
        if (HotkeyGesture.TryParse(now.MarkerHotkey, out var g)) now = now with { MarkerHotkey = g!.ToString() };
        try
        {
            AppHost.Settings.Save(now);
        }
        catch (Exception ex)
        {
            Show("Could not save settings", ex.Message, InfoBarSeverity.Error);
            return;
        }
        var problems = ((App)System.Windows.Application.Current).ApplyRuntimeSettings(old, now);
        LoadFrom(AppHost.Settings.Current);
        if (!AppHost.Services.IsRunning)
            Show("Recording is stopped", string.Join("\n", problems), InfoBarSeverity.Error);
        else if (problems.Count > 0) Show("Saved with warnings", string.Join("\n", problems), InfoBarSeverity.Warning);
        else Show("Settings applied", NeedsRestart(old, now) ? "The recording pipeline was restarted." : "All changes are live.", InfoBarSeverity.Success);
    }

    public static bool NeedsRestart(AppSettings a, AppSettings b) =>
        a.BufferMinutes != b.BufferMinutes || a.SampleIntervalSeconds != b.SampleIntervalSeconds
        || !string.Equals(a.DataFolder, b.DataFolder, StringComparison.OrdinalIgnoreCase);

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show("Reset all settings to their defaults?", "Hindsight",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (result != System.Windows.MessageBoxResult.OK) return;
        LoadFrom(AppSettings.Default with { WindowBounds = AppHost.Settings.Current.WindowBounds });
        Apply_Click(sender, e);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the data folder", InitialDirectory = DataFolder.Text };
        if (dlg.ShowDialog() == true) DataFolder.Text = dlg.FolderName;
    }

    /// <summary>Live preview only (not saved): collapses the ranking-weight rows, the idle threshold and the foreground
    /// toggle while "Easy" is selected. The Detection spike thresholds stay visible in both levels.</summary>
    private void Level_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyLevelVisibility((DetailLevel)Math.Max(0, Level.SelectedIndex));

    private void ApplyLevelVisibility(DetailLevel level)
    {
        var v = level == DetailLevel.Professional ? Visibility.Visible : Visibility.Collapsed;
        WCpuRow.Visibility = v; WIoRow.Visibility = v; WNetRow.Visibility = v; WMemRow.Visibility = v; WGpuRow.Visibility = v;
        IdleAfterRow.Visibility = v; ShowForegroundRow.Visibility = v;
    }

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        Info.Title = title; Info.Message = text; Info.Severity = severity; Info.IsOpen = true;
    }

    /// <summary>Records the pressed combination as the marker hotkey (modifier + key), instead of typed text.</summary>
    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        string? name = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            >= Key.F1 and <= Key.F12 => key.ToString(),
            Key.Space => "Space", Key.Pause => "Pause", Key.Insert => "Insert", Key.Delete => "Delete",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            _ => null,
        };
        if (name is null) return;
        var mods = Keyboard.Modifiers;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            Show("Hotkey without modifier", $"{name} will no longer reach other applications while Hindsight runs.", InfoBarSeverity.Informational);
        }
        parts.Add(name);
        Hotkey.Text = string.Join("+", parts);
    }
}
