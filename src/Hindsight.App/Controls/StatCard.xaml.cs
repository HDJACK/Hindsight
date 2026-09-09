using System.Windows;
using System.Windows.Controls;

namespace Hindsight.App.Controls;

public partial class StatCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(StatCard),
            new PropertyMetadata("", (d, e) => ((StatCard)d).TitleText.Text = (string)e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public StatCard() { InitializeComponent(); }

    public void Update(string valueText, double sparkValue)
    {
        ValueText.Text = valueText;
        Spark.Push(sparkValue);
    }
}
