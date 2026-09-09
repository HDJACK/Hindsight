namespace Hindsight.Core.Analysis;

/// <summary>Median and median-absolute-deviation baselines, which unlike a mean survive the spikes they are used to find.</summary>
public static class Baseline
{
    /// <summary>NaN-free input expected; empty → 0.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    public static double Mad(IReadOnlyList<double> values, double median)
    {
        if (values.Count == 0) return 0;
        var dev = new double[values.Count];
        for (int i = 0; i < values.Count; i++) dev[i] = Math.Abs(values[i] - median);
        return Median(dev);
    }

    /// <summary>Block-rolling baseline: step = max(1, window/10). For block b (indices [b*step, (b+1)*step)) the baseline uses the non-NaN values in [b*step - window, b*step). When fewer than 10 such values exist, it uses the non-NaN values in [0, min(n, window)) instead (warm-up). Returns arrays of length n.</summary>
    public static (double[] Median, double[] Mad) Rolling(double[] values, int window)
    {
        int n = values.Length;
        var med = new double[n]; var mad = new double[n];
        if (n == 0) return (med, mad);
        window = Math.Max(1, window);
        int step = Math.Max(1, window / 10);
        var buf = new List<double>(window);
        List<double>? warm = null;
        for (int start = 0; start < n; start += step)
        {
            buf.Clear();
            for (int i = Math.Max(0, start - window); i < start; i++) if (!double.IsNaN(values[i])) buf.Add(values[i]);
            IReadOnlyList<double> src = buf;
            if (buf.Count < 10)
            {
                warm ??= Enumerable.Range(0, Math.Min(n, window)).Select(i => values[i]).Where(v => !double.IsNaN(v)).ToList();
                src = warm;
            }
            double m = Median(src), d = Mad(src, m);
            for (int i = start; i < Math.Min(n, start + step); i++) { med[i] = m; mad[i] = d; }
        }
        return (med, mad);
    }
}
