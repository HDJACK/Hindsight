using System.Windows;
using System.Windows.Controls;
using Hindsight.Core.Model;

namespace Hindsight.App.Controls;

/// <summary>Horizontal row of checkboxes, one per <see cref="LaneKind"/>, driving which timeline lanes are visible.</summary>
public partial class LaneStrip : UserControl
{
    private readonly List<CheckBox> _boxes = new();
    private bool _suppress;

    public event Action<IReadOnlyList<LaneKind>>? SelectionChanged;

    public LaneStrip()
    {
        InitializeComponent();
        foreach (var spec in TimelineControl.AllLanes)
        {
            var box = new CheckBox { Content = spec.Label, Margin = new Thickness(0, 0, 12, 4), Tag = spec.Kind };
            box.Checked += OnToggled;
            box.Unchecked += OnToggled;
            _boxes.Add(box);
            Panel.Children.Add(box);
        }
    }

    /// <summary>Sets the checked state of each lane checkbox without firing <see cref="SelectionChanged"/>.</summary>
    public void SetSelection(IReadOnlyList<LaneKind> kinds)
    {
        _suppress = true;
        try
        {
            foreach (var box in _boxes) box.IsChecked = kinds.Contains((LaneKind)box.Tag!);
        }
        finally { _suppress = false; }
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        var selected = _boxes.Where(b => b.IsChecked == true).Select(b => (LaneKind)b.Tag!).ToArray();
        if (selected.Length == 0)
        {
            // Keep at least one lane checked: re-check the box that was just unchecked.
            _suppress = true;
            try { ((CheckBox)sender).IsChecked = true; }
            finally { _suppress = false; }
            selected = new[] { (LaneKind)((CheckBox)sender).Tag! };
        }
        var ordered = TimelineControl.AllLanes.Select(l => l.Kind).Where(selected.Contains).ToArray();
        SelectionChanged?.Invoke(ordered);
    }
}
