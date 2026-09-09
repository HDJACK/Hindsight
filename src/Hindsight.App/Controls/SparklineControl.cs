using System.Windows;
using System.Windows.Media;

namespace Hindsight.App.Controls;

public sealed class SparklineControl : FrameworkElement
{
    private const int Capacity = 60;
    private readonly List<double> _values = new();

    public void Push(double v)
    {
        _values.Add(v);
        if (_values.Count > Capacity) _values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        var pen = new Pen(brush, 1.5); pen.Freeze();
        double max = Math.Max(1e-9, _values.Max());
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i < _values.Count; i++)
            {
                var p = new Point((Capacity - _values.Count + i) / (double)(Capacity - 1) * ActualWidth, ActualHeight - _values[i] / max * (ActualHeight - 2) - 1);
                if (i == 0) ctx.BeginFigure(p, false, false); else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}
