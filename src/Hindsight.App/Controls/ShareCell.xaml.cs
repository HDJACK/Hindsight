using System.Windows;
using System.Windows.Controls;

namespace Hindsight.App.Controls;

/// <summary>One share cell of the Overview/Range grids: a 0–100 bar with the pre-formatted figure beside it.
/// Factored out so the two grids share one definition per metric instead of repeating the same markup twice.</summary>
public partial class ShareCell : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(ShareCell), new PropertyMetadata(0d));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(ShareCell), new PropertyMetadata(""));

    public ShareCell() => InitializeComponent();

    /// <summary>The share as a percentage (0–100).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>The figure shown to the right of the bar, already formatted.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
