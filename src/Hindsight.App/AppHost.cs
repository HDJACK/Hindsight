using Hindsight.App.Services;
using Hindsight.Core.Model;
using Hindsight.Core.Settings;

namespace Hindsight.App;

/// <summary>Process-wide access to the few long-lived services. Pages are created by the NavigationView and read these.</summary>
public static class AppHost
{
    public static SettingsStore Settings { get; set; } = null!;
    public static AppServices Services { get; set; } = null!;
    public static ThemeService Theme { get; set; } = null!;
    public static MainWindow? Window { get; set; }
    public static Pages.LivePage? Live { get; set; }
    public static Pages.AnalysisPage? Analysis { get; set; }
    public static Action<Pages.AnalysisPage>? PendingAnalysis { get; set; }

    public static DetailLevel DetailLevel => Settings.Current.DetailLevel;

    /// <summary>Raised when the Easy/Professional detail level changes, either from Settings → Apply or the status-bar switch.</summary>
    public static event Action? DetailLevelChanged;

    public static void RaiseDetailLevelChanged() => DetailLevelChanged?.Invoke();
}
