namespace Hindsight.Core.Model;

/// <summary>One minute-bucketed context observation (DNS query, endpoint connection, or file access) for a process.</summary>
public sealed record ContextEntry(long Minute, int Pid, long StartTime, ContextKind Kind, string Key, long Value)
{
    public static long MinuteOf(long unix) => unix - unix % 60;
}
