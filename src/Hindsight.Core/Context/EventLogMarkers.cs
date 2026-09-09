using Hindsight.Core.Model;

namespace Hindsight.Core.Context;

/// <summary>Maps Windows event-log records (log name + event id + property strings) to markers.</summary>
public static class EventLogMarkers
{
    public const string WindowsUpdateLog = "Microsoft-Windows-WindowsUpdateClient/Operational";
    public const string DefenderLog = "Microsoft-Windows-Windows Defender/Operational";
    public const string TaskSchedulerLog = "Microsoft-Windows-TaskScheduler/Operational";

    /// <summary>Event ids we subscribe to, per log, as an XPath predicate.</summary>
    public const string WindowsUpdateQuery = "*[System[(EventID=41 or EventID=43 or EventID=44)]]";
    public const string DefenderQuery = "*[System[(EventID=1000 or EventID=1001)]]";
    public const string TaskSchedulerQuery = "*[System[(EventID=100)]]";

    private const int MaxTitleLength = 60;

    /// <summary>Maps one event record to a marker; null for ids we do not care about. props = the record's property values as strings.</summary>
    public static Marker? Map(string logName, int eventId, IReadOnlyList<string> props, long unixTime)
    {
        switch (logName)
        {
            case WindowsUpdateLog:
                string? prefix = eventId switch
                {
                    41 => "Windows Update: download started",
                    43 => "Windows Update: install started",
                    44 => "Windows Update: installed",
                    _ => null,
                };
                if (prefix is null) return null;
                string title = Title(props);
                return new Marker(unixTime, MarkerKind.WindowsUpdate, title.Length == 0 ? prefix : prefix + " · " + title);

            case DefenderLog:
                return eventId switch
                {
                    1000 => new Marker(unixTime, MarkerKind.DefenderScan, "Defender scan started"),
                    1001 => new Marker(unixTime, MarkerKind.DefenderScan, "Defender scan finished"),
                    _ => null,
                };

            case TaskSchedulerLog:
                if (eventId != 100 || props.Count == 0) return null;
                string task = props[0].Trim();
                if (task.Length == 0) return null;
                return new Marker(unixTime, MarkerKind.ScheduledTask, "Task: " + task);

            default:
                return null;
        }
    }

    /// <summary>First non-empty property, trimmed and capped at 60 characters.</summary>
    private static string Title(IReadOnlyList<string> props)
    {
        for (int i = 0; i < props.Count; i++)
        {
            string p = props[i].Trim();
            if (p.Length == 0) continue;
            return p.Length > MaxTitleLength ? p[..MaxTitleLength] : p;
        }
        return "";
    }
}
