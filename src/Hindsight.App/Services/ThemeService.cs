using System.Windows;
using Hindsight.Core.Settings;
using Wpf.Ui.Appearance;

namespace Hindsight.App.Services;

public sealed class ThemeService
{
    private readonly Window _window;
    private bool _watching;

    public ThemeService(Window window) => _window = window;

    public void Apply(AppTheme theme)
    {
        switch (theme)
        {
            case AppTheme.System:
                // Wpf.Ui.Appearance.ApplicationThemeManager has no ApplySystemTheme member in 4.3.0.
                // SystemThemeWatcher.Watch() itself "applies the background effect and theme
                // according to the system theme" (Wpf.Ui.xml, SystemThemeWatcher.Watch), so it
                // both performs the initial apply and keeps following later OS theme changes.
                if (!_watching) { SystemThemeWatcher.Watch(_window); _watching = true; }
                break;
            case AppTheme.Light:
                StopWatching();
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case AppTheme.Dark:
                StopWatching();
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
        }
    }

    private void StopWatching()
    {
        if (!_watching) return;
        SystemThemeWatcher.UnWatch(_window);
        _watching = false;
    }
}
