using Hindsight.Core.Model;
using Hindsight.Core.Settings;

namespace Hindsight.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_MatchSpec()
    {
        var s = AppSettings.Default;
        Assert.Equal(30, s.BufferMinutes);
        Assert.Equal(1, s.SampleIntervalSeconds);
        Assert.EndsWith("Hindsight", s.DataFolder);
        Assert.Equal(80, s.CpuSpikePercent);
        Assert.Equal(300, s.FileIoSpikeMBps);
        Assert.Equal(50, s.NetworkSpikeMBps);
        Assert.Equal(300, s.BaselineWindowSeconds);
        Assert.Equal((1d, 1d, 1d, 0d), (s.WeightCpu, s.WeightIo, s.WeightNet, s.WeightMemory));
        Assert.Equal("Ctrl+Alt+M", s.MarkerHotkey);
        Assert.False(s.StartWithWindows);
        Assert.Equal(AppTheme.System, s.Theme);
        Assert.True(s.StoreCommandLines);
        Assert.Null(s.WindowBounds);
        Assert.Equal(90, s.GpuSpikePercent);
        Assert.Equal(50, s.DiskLatencySpikeMs);
        Assert.Equal(90, s.CommitSpikePercent);
        Assert.Equal(1, s.WeightGpu);
        Assert.Equal(60, s.IdleAfterSeconds);
        Assert.True(s.ShowForegroundApp);
        Assert.Equal(DetailLevel.Easy, s.DetailLevel);
        Assert.Equal("Cpu,Gpu,FileIo,Network,RamFree", s.VisibleLanes);
        Assert.Equal(new[] { LaneKind.Cpu, LaneKind.Gpu, LaneKind.FileIo, LaneKind.Network, LaneKind.RamFree }, s.VisibleLaneKinds());
        Assert.Equal(AnomalySensitivity.Normal, s.AnomalySensitivity);
        Assert.Equal(2, s.MinAnomalySeconds);
        Assert.Equal(4, s.SensitivityK);
        Assert.True(s.CaptureFilePaths); Assert.True(s.CaptureDnsAndEndpoints);
        Assert.Empty(SettingsValidator.Validate(s));
    }

    [Theory]
    [InlineData(4, true)] [InlineData(5, false)] [InlineData(180, false)] [InlineData(181, true)]
    public void Validate_BufferMinutesRange(int minutes, bool error)
    {
        var errors = SettingsValidator.Validate(AppSettings.Default with { BufferMinutes = minutes });
        Assert.Equal(error, errors.Any(e => e.Contains("Buffer length")));
    }

    [Fact]
    public void Validate_ReportsEveryBadField()
    {
        var bad = AppSettings.Default with
        {
            SampleIntervalSeconds = 0, CpuSpikePercent = 5, FileIoSpikeMBps = 0, NetworkSpikeMBps = 0,
            BaselineWindowSeconds = 10, WeightCpu = 11, MarkerHotkey = "Banana", DataFolder = "",
            GpuSpikePercent = 5, DiskLatencySpikeMs = 0, CommitSpikePercent = 10, WeightGpu = 11, IdleAfterSeconds = 1,
            MinAnomalySeconds = 0
        };
        var errors = SettingsValidator.Validate(bad);
        Assert.Equal(14, errors.Count);
    }

    [Fact]
    public void SensitivityK_PerLevel()
    {
        Assert.Equal(6, (AppSettings.Default with { AnomalySensitivity = AnomalySensitivity.Low }).SensitivityK);
        Assert.Equal(3, (AppSettings.Default with { AnomalySensitivity = AnomalySensitivity.High }).SensitivityK);
        Assert.Equal(1, SettingsValidator.Clamp(AppSettings.Default with { MinAnomalySeconds = -5 }).MinAnomalySeconds);
    }

    [Fact]
    public void Clamp_PullsValuesIntoRange()
    {
        var c = SettingsValidator.Clamp(AppSettings.Default with
        {
            BufferMinutes = 999, SampleIntervalSeconds = 0, WeightNet = -3, MarkerHotkey = "???",
            GpuSpikePercent = 5, DiskLatencySpikeMs = 0, CommitSpikePercent = 10, WeightGpu = 11, IdleAfterSeconds = 1
        });
        Assert.Equal(180, c.BufferMinutes);
        Assert.Equal(1, c.SampleIntervalSeconds);
        Assert.Equal(0, c.WeightNet);
        Assert.Equal("Ctrl+Alt+M", c.MarkerHotkey);
        Assert.Equal(10, c.GpuSpikePercent);
        Assert.Equal(1, c.DiskLatencySpikeMs);
        Assert.Equal(50, c.CommitSpikePercent);
        Assert.Equal(10, c.WeightGpu);
        Assert.Equal(10, c.IdleAfterSeconds);
        Assert.Empty(SettingsValidator.Validate(c));
    }

    [Fact]
    public void VisibleLanes_ParsesAndDropsUnknown()
    {
        var s = AppSettings.Default with { VisibleLanes = "Gpu,Bogus,Cpu" };
        Assert.Equal(new[] { LaneKind.Gpu, LaneKind.Cpu }, s.VisibleLaneKinds());

        var empty = AppSettings.Default with { VisibleLanes = "" };
        Assert.Equal(AppSettings.Default.VisibleLaneKinds(), empty.VisibleLaneKinds());

        var dup = AppSettings.Default with { VisibleLanes = "Cpu,Cpu,Gpu" };
        Assert.Equal(new[] { LaneKind.Cpu, LaneKind.Gpu }, dup.VisibleLaneKinds());

        Assert.Equal("Cpu,Commit", AppSettings.LanesToString(new[] { LaneKind.Cpu, LaneKind.Commit }));
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsAndNotReset()
    {
        using var f = new TempFile();
        var store = new SettingsStore(f.Path);
        var r = store.Load();
        Assert.False(r.WasReset);
        Assert.Equal(AppSettings.Default, r.Settings);
        Assert.Equal(AppSettings.Default, store.Current);
        Assert.True(File.Exists(f.Path));
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips_AndRaisesChanged()
    {
        using var f = new TempFile();
        var store = new SettingsStore(f.Path);
        AppSettings? seen = null;
        store.Changed += s => seen = s;
        var custom = AppSettings.Default with { BufferMinutes = 60, Theme = AppTheme.Dark, MarkerHotkey = "Ctrl+Shift+F9", WindowBounds = new WindowBounds(1, 2, 3, 4) };
        store.Save(custom);
        Assert.Equal(custom, seen);
        var again = new SettingsStore(f.Path).Load();
        Assert.Equal(custom, again.Settings);
        Assert.Contains("\"Theme\": \"Dark\"", File.ReadAllText(f.Path));
    }

    [Fact]
    public void Load_CorruptFile_ResetsToDefaults()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, "{ not json");
        var r = new SettingsStore(f.Path).Load();
        Assert.True(r.WasReset);
        Assert.Equal(AppSettings.Default, r.Settings);
        Assert.NotEmpty(r.Notes);
    }

    [Fact]
    public void Load_OutOfRange_IsClampedAndNoted()
    {
        using var f = new TempFile();
        File.WriteAllText(f.Path, "{ \"BufferMinutes\": 5000, \"Theme\": \"Light\" }");
        var r = new SettingsStore(f.Path).Load();
        Assert.False(r.WasReset);
        Assert.Equal(180, r.Settings.BufferMinutes);
        Assert.Equal(AppTheme.Light, r.Settings.Theme);
        Assert.Contains(r.Notes, n => n.Contains("Buffer length"));
    }
}
