namespace Hindsight.Core.Model;

/// <summary>StartTime is a FILETIME (100-ns ticks since 1601 UTC); together with Pid it is unique.</summary>
public sealed record ProcessIdentity(int Pid, long StartTime, string Name, string Path, string CommandLine, int ParentPid,
    string Services = "", string Description = "", string Company = "")
{
    public static ProcessIdentity Placeholder(int pid, long startTime) =>
        new(pid, startTime, $"PID {pid}", "", "", 0);

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => Services.Length == 0 ? Name : $"{Name} ({Services})";
}
