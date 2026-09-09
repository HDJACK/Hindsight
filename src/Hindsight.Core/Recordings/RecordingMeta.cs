namespace Hindsight.Core.Recordings;

/// <summary>Header of a recording file: what was recorded, when, and by which version.</summary>
public sealed record RecordingMeta(
    int FormatVersion, string Name, string Notes, long StartUnix, long EndUnix,
    int IntervalSeconds, int CoreCount, string MachineName, int TickCount, string AppVersion)
{
    public const int CurrentFormatVersion = 3;
    public long DurationSeconds => TickCount == 0 ? 0 : EndUnix - StartUnix + IntervalSeconds;
}
