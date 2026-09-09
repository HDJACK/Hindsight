using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class AutostartTaskTests
{
    [Fact]
    public void CreateArguments_RunsAtLogonWithHighestPrivileges_AndQuotesPath()
    {
        var a = AutostartTask.CreateArguments(@"C:\Program Files\Hindsight\Hindsight.App.exe");
        Assert.Contains("/Create", a);
        Assert.Contains("/TN \"Hindsight\"", a);
        Assert.Contains("/SC ONLOGON", a);
        Assert.Contains("/RL HIGHEST", a);
        Assert.Contains("/F", a);
        Assert.Contains("/TR \"\\\"C:\\Program Files\\Hindsight\\Hindsight.App.exe\\\"\"", a);
    }

    [Fact]
    public void DeleteAndQueryArguments_TargetTaskName()
    {
        Assert.Equal("/Delete /TN \"Hindsight\" /F", AutostartTask.DeleteArguments());
        Assert.Equal("/Query /TN \"Hindsight\"", AutostartTask.QueryArguments());
    }

    [Fact]
    public void PowerInfo_ReturnsAValue()
    {
        Assert.NotNull(PowerInfo.IsOnAcPower());
    }
}
