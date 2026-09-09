using Hindsight.Core.Context;
using Hindsight.Core.Model;

namespace Hindsight.Core.Tests;

public class EventLogMarkersTests
{
    [Theory]
    [InlineData(EventLogMarkers.WindowsUpdateLog, 41, "KB5001", MarkerKind.WindowsUpdate, "Windows Update: download started · KB5001")]
    [InlineData(EventLogMarkers.WindowsUpdateLog, 43, "", MarkerKind.WindowsUpdate, "Windows Update: install started")]
    [InlineData(EventLogMarkers.WindowsUpdateLog, 44, "Cumulative Update", MarkerKind.WindowsUpdate, "Windows Update: installed · Cumulative Update")]
    [InlineData(EventLogMarkers.DefenderLog, 1000, "x", MarkerKind.DefenderScan, "Defender scan started")]
    [InlineData(EventLogMarkers.DefenderLog, 1001, "x", MarkerKind.DefenderScan, "Defender scan finished")]
    [InlineData(EventLogMarkers.TaskSchedulerLog, 100, "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", MarkerKind.ScheduledTask, "Task: \\Microsoft\\Windows\\Defrag\\ScheduledDefrag")]
    public void Map_KnownIds(string log, int id, string prop, MarkerKind kind, string text)
    {
        var m = EventLogMarkers.Map(log, id, new[] { prop }, 1234)!;
        Assert.Equal(kind, m.Kind); Assert.Equal(text, m.Text); Assert.Equal(1234, m.UnixTime);
    }

    [Fact]
    public void Map_UnknownOrLongTitle()
    {
        Assert.Null(EventLogMarkers.Map(EventLogMarkers.WindowsUpdateLog, 19, new[] { "x" }, 1));
        Assert.Null(EventLogMarkers.Map("Other/Log", 41, new[] { "x" }, 1));
        var m = EventLogMarkers.Map(EventLogMarkers.WindowsUpdateLog, 44, new[] { "", new string('t', 100) }, 1)!;
        Assert.EndsWith(new string('t', 60), m.Text);
        Assert.Null(EventLogMarkers.Map(EventLogMarkers.TaskSchedulerLog, 100, Array.Empty<string>(), 1));
        Assert.Null(EventLogMarkers.Map(EventLogMarkers.DefenderLog, 1002, new[] { "x" }, 1));
        Assert.Null(EventLogMarkers.Map(EventLogMarkers.TaskSchedulerLog, 200, new[] { "x" }, 1));
    }

    [Fact]
    public void Map_LongTaskName()
    {
        var longPath = new string('\\', 90);
        var m = EventLogMarkers.Map(EventLogMarkers.TaskSchedulerLog, 100, new[] { longPath }, 1)!;
        Assert.Equal(MarkerKind.ScheduledTask, m.Kind);
        Assert.Equal("Task: " + longPath, m.Text);
        Assert.Equal(1, m.UnixTime);
    }
}
