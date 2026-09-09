namespace Hindsight.Core.Analysis;

/// <summary>An inclusive range of unix seconds.</summary>
public readonly record struct TimeRange(long FromUnix, long ToUnix)
{
    public long Seconds => Math.Max(0, ToUnix - FromUnix + 1);
    public bool Contains(long unix) => unix >= FromUnix && unix <= ToUnix;
    public static TimeRange Of(long a, long b) => a <= b ? new(a, b) : new(b, a);
}
