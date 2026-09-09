using System.Windows;
using System.Windows.Controls;

namespace Hindsight.App.Controls;

/// <summary>Fit / Follow / "Use view as range" buttons plus the gesture hint for a <see cref="TimelineControl"/>.</summary>
public partial class TimelineToolbar : UserControl
{
    private TimelineControl? _target;
    private bool _suppress;

    public TimelineToolbar() { InitializeComponent(); }

    /// <summary>The timeline the buttons act on; its <see cref="TimelineControl.ViewChanged"/> keeps the follow toggle in sync.</summary>
    public TimelineControl? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            if (_target is not null) _target.ViewChanged -= SyncFollow;
            _target = value;
            if (_target is not null) _target.ViewChanged += SyncFollow;
            SyncFollow();
        }
    }

    /// <summary>Shows the follow toggle (live sources only).</summary>
    public bool ShowFollow
    {
        set => FollowToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised when "Use view as range" is clicked (wired by the analysis page).</summary>
    public event Action? UseViewAsRange;

    private void SyncFollow()
    {
        _suppress = true;
        try { FollowToggle.IsChecked = _target?.Follow == true; }
        finally { _suppress = false; }
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => _target?.Fit();

    private void Follow_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _target?.SetFollow(FollowToggle.IsChecked == true);
    }

    private void Range_Click(object sender, RoutedEventArgs e)
    {
        _target?.SelectViewAsRange();
        UseViewAsRange?.Invoke();
    }
}
