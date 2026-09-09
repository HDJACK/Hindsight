using System.ComponentModel;
using System.Windows;
using Hindsight.App.Pages;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using Wpf.Ui.Controls;

namespace Hindsight.App;

public partial class MainWindow : FluentWindow
{
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RootNavigation.Navigate(typeof(LivePage));
            UpdateStatus();
            ThemeButton.Content = "Theme: " + AppHost.Settings.Current.Theme;
            UpdateLevelButton();
        };
        Closing += OnClosing;
        AppHost.DetailLevelChanged += UpdateLevelButton;
    }

    private void LevelButton_Click(object sender, RoutedEventArgs e)
    {
        var current = AppHost.Settings.Current;
        var next = current.DetailLevel == DetailLevel.Easy ? DetailLevel.Professional : DetailLevel.Easy;
        AppHost.Settings.Save(current with { DetailLevel = next });
        AppHost.RaiseDetailLevelChanged();
    }

    private void UpdateLevelButton()
    {
        bool pro = AppHost.DetailLevel == DetailLevel.Professional;
        LevelButton.Content = pro ? "Pro" : "Easy";
        TickText.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var current = AppHost.Settings.Current;
        var next = current.Theme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System,
        };
        AppHost.Settings.Save(current with { Theme = next });
        AppHost.Theme.Apply(next);
        ThemeButton.Content = "Theme: " + next;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveBounds();
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>Closes the global info bar when it currently shows the given title.</summary>
    public void ClearMessage(string title)
    {
        if (GlobalInfo.IsOpen && string.Equals(GlobalInfo.Title, title, StringComparison.Ordinal)) GlobalInfo.IsOpen = false;
    }

    public void ShowMessage(string title, string text, InfoBarSeverity severity)
    {
        GlobalInfo.Title = title;
        GlobalInfo.Message = text;
        GlobalInfo.Severity = severity;
        GlobalInfo.IsOpen = true;
    }

    public void UpdateStatus()
    {
        string text;
        if (!AppHost.Services.IsRunning)
        {
            text = "Stopped · recording pipeline failed, check Settings";
        }
        else
        {
            var r = AppHost.Services.Recorder;
            text = r.EtwActive
                ? "Recording · ETW active"
                : "Recording · ETW unavailable: " + (r.DegradedReason ?? "unknown");
        }
        var counters = AppHost.Services.MissingCounters;
        var notes = AppHost.Services.ContextNoteSnapshot;
        var missing = counters.Concat(notes).ToList();
        if (counters.Count > 0)
            text += counters.Any(m => m.Contains("GPU")) ? " · GPU counters unavailable" : " · Some counters unavailable";
        if (notes.Count > 0)
            text += notes.Any(n => n.Contains("File paths disabled")) ? " · File paths disabled" : " · Some context unavailable";
        StatusText.Text = text;
        StatusText.ToolTip = missing.Count > 0 ? string.Join(Environment.NewLine, missing) : null;
    }

    public void OnTick(Tick t)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TickText.Text = $"{Format.Time(t.UnixTime)} · {t.ProcessCount} processes" + (t.LostEvents > 0 ? $" · {t.LostEvents} lost events" : "");
            UpdateRecordingIndicator();
        });
    }

    public void NavigateTo(Type page) => RootNavigation.Navigate(page);

    public void OpenRecording(string path)
    {
        AppHost.PendingAnalysis = p => p.ShowRecording(path);
        NavigateTo(typeof(AnalysisPage));
        Dispatcher.BeginInvoke(() => AppHost.Analysis?.ConsumePending());
    }

    public void OpenCompare(string a, string b)
    {
        AppHost.PendingAnalysis = p => p.ShowCompare(a, b);
        NavigateTo(typeof(AnalysisPage));
        Dispatcher.BeginInvoke(() => AppHost.Analysis?.ConsumePending());
    }

    public void UpdateRecordingIndicator()
    {
        var r = AppHost.Services.Recordings;
        if (!r.IsRecording || r.StartedAt is null) { RecText.Visibility = Visibility.Collapsed; return; }
        RecText.Text = $"● REC {r.CurrentName} · {Format.Duration((long)(DateTimeOffset.Now - r.StartedAt.Value).TotalSeconds)}";
        RecText.Visibility = Visibility.Visible;
    }

    public void ApplyBounds(WindowBounds? b)
    {
        if (b is null) return;
        Left = b.Left; Top = b.Top; Width = Math.Max(MinWidth, b.Width); Height = Math.Max(MinHeight, b.Height);
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        var s = AppHost.Settings.Current with { WindowBounds = new WindowBounds(Left, Top, Width, Height) };
        try { AppHost.Settings.Save(s); } catch { /* settings invalid: keep old bounds */ }
    }
}
