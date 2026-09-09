using Hindsight.Core.Context;
using Hindsight.Core.Model;

namespace Hindsight.Core.Tests;

public class ContextAccumulatorTests
{
    [Fact]
    public void Flush_TopTenPerPidAndKind_SortedByValue()
    {
        var a = new ContextAccumulator();
        for (int i = 0; i < 15; i++) a.Add(5, ContextKind.File, $"C:\\f{i}", 100 - i);
        a.Add(5, ContextKind.File, "C:\\f0", 5);               // accumulates: 105
        a.Add(5, ContextKind.Dns, "a.example", 1);
        a.Add(6, ContextKind.Endpoint, "1.2.3.4:443", 10);
        var list = a.Flush(1200, pid => pid == 5 ? 77 : 0);
        var files = list.Where(e => e.Pid == 5 && e.Kind == ContextKind.File).ToList();
        Assert.Equal(10, files.Count);
        Assert.Equal("C:\\f0", files[0].Key); Assert.Equal(105, files[0].Value); Assert.Equal(77, files[0].StartTime); Assert.Equal(1200, files[0].Minute);
        Assert.Equal("C:\\f9", files[9].Key);
        Assert.Single(list, e => e.Kind == ContextKind.Dns);
        Assert.Equal(0, list.Single(e => e.Pid == 6).StartTime);
        Assert.True(a.IsEmpty);
        Assert.Empty(a.Flush(1260, _ => 0));
    }

    [Fact]
    public void Add_CapsKeysPerBucket_IntoOther_AndTruncatesLongKeys()
    {
        var a = new ContextAccumulator();
        for (int i = 0; i < 250; i++) a.Add(1, ContextKind.Dns, $"h{i}.example", 1);
        a.Add(1, ContextKind.Dns, new string('x', 400), 1);
        var list = a.Flush(0, _ => 0);
        var other = list.Single(e => e.Key == ContextAccumulator.OtherKey);
        Assert.Equal(51, other.Value);                              // 50 overflow keys + the long one (also overflow)
        Assert.All(list, e => Assert.True(e.Key.Length <= ContextAccumulator.MaxKeyLength));
    }

    [Fact]
    public void Add_IgnoresInvalid_AndIsThreadSafe()
    {
        var a = new ContextAccumulator();
        a.Add(0, ContextKind.Dns, "x", 1); a.Add(1, ContextKind.Dns, "", 1); a.Add(1, ContextKind.Dns, "x", 0);
        Assert.True(a.IsEmpty);
        Parallel.For(0, 10_000, i => a.Add(1 + i % 4, ContextKind.Endpoint, $"10.0.0.{i % 8}:80", 1));
        var list = a.Flush(0, _ => 0);
        Assert.Equal(10_000, list.Sum(e => e.Value));
    }
}
