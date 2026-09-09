using Hindsight.Core.Settings;
using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class HostStateSourceTests
{
    private static AppSettings DefaultSettings => AppSettings.Default;

    [Fact]
    public void Read_NotIdle_WhenBelowThreshold()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 1234,
            lastInputTick: () => 0,
            nowTick: () => 59_000,
            power: () => (true, (sbyte)80));
        src.Start();

        var snap = src.Read();

        Assert.False(snap.Totals.UserIdle);
    }

    [Fact]
    public void Read_Idle_WhenAboveThreshold()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 1234,
            lastInputTick: () => 0,
            nowTick: () => 61_000,
            power: () => (true, (sbyte)80));
        src.Start();

        var snap = src.Read();

        Assert.True(snap.Totals.UserIdle);
    }

    [Fact]
    public void Read_UsesConfiguredIdleThreshold()
    {
        var settings = AppSettings.Default with { IdleAfterSeconds = 10 };
        using var src = new HostStateSource(
            () => settings,
            foregroundPid: () => 1234,
            lastInputTick: () => 0,
            nowTick: () => 11_000,
            power: () => (true, (sbyte)80));
        src.Start();

        var snap = src.Read();

        Assert.True(snap.Totals.UserIdle);
    }

    [Fact]
    public void Read_PassesThroughForegroundPid()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 4321,
            lastInputTick: () => 0,
            nowTick: () => 0,
            power: () => (true, (sbyte)80));
        src.Start();

        var snap = src.Read();

        Assert.Equal(4321, snap.Totals.ForegroundPid);
    }

    [Fact]
    public void Read_MapsUnknownBatteryTo255()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 0,
            lastInputTick: () => 0,
            nowTick: () => 0,
            power: () => (null, (sbyte)-1));
        src.Start();

        var snap = src.Read();

        Assert.Equal(-1, snap.Totals.BatteryPercent);
        Assert.Null(snap.Totals.OnAc);
    }

    [Fact]
    public void Read_PassesThroughBatteryPercentAndOnAc()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 0,
            lastInputTick: () => 0,
            nowTick: () => 0,
            power: () => (false, (sbyte)42));
        src.Start();

        var snap = src.Read();

        Assert.Equal(42, snap.Totals.BatteryPercent);
        Assert.False(snap.Totals.OnAc);
    }

    [Fact]
    public void Read_BeforeStart_StillWorks()
    {
        using var src = new HostStateSource(
            () => DefaultSettings,
            foregroundPid: () => 1234,
            lastInputTick: () => 0,
            nowTick: () => 0,
            power: () => (true, (sbyte)80));

        var snap = src.Read();

        Assert.Equal(1234, snap.Totals.ForegroundPid);
    }

    [Fact]
    public void Name_IsHost()
    {
        using var src = new HostStateSource(() => DefaultSettings);
        Assert.Equal("host", src.Name);
    }
}
