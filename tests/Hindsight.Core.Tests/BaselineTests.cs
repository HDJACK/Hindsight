using Hindsight.Core.Analysis;

namespace Hindsight.Core.Tests;

public class BaselineTests
{
    [Fact]
    public void Median_And_Mad()
    {
        var v = new double[] { 1, 2, 3, 4, 100 };
        Assert.Equal(3, Baseline.Median(v));
        Assert.Equal(1, Baseline.Mad(v, 3));          // |1-3|,|2-3|,0,1,97 → sorted 0,1,1,2,97 → 1
        Assert.Equal(2.5, Baseline.Median(new double[] { 1, 2, 3, 4 }));
        Assert.Equal(0, Baseline.Median(Array.Empty<double>()));
    }

    [Fact]
    public void Rolling_UsesPrecedingWindowAndSkipsNaN()
    {
        // 40 values: 0..19 = 10, 20..39 = 50 (NaN at index 5). window 20 → step 2.
        var v = Enumerable.Range(0, 40).Select(i => i < 20 ? 10.0 : 50.0).ToArray();
        v[5] = double.NaN;
        var (med, mad) = Baseline.Rolling(v, 20);
        Assert.Equal(40, med.Length);
        Assert.Equal(10, med[25]);   // block starting at 24 uses [4,24): 15 values of 10 → 10
        Assert.Equal(0, mad[25]);
        Assert.Equal(50, med[39]);   // block starting at 38 uses [18,38): two 10s and eighteen 50s → 50
    }

    [Fact]
    public void Rolling_WarmUpUsesLeadingValues()
    {
        var v = new double[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 90, 5, 5 };
        var (med, _) = Baseline.Rolling(v, 300);   // step 30 → single block, fewer than 10 preceding → warm-up over all 15
        Assert.All(med, m => Assert.Equal(5, m));
    }

    [Fact]
    public void Rolling_Fast()
    {
        var v = Enumerable.Range(0, 10_800).Select(i => (double)(i % 37)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 9; i++) Baseline.Rolling(v, 300);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }
}
