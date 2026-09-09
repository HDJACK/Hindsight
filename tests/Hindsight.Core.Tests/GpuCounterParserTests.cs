using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class GpuCounterParserTests
{
    [Fact]
    public void TryParseInstance_ValidInstance_ReturnsPidAndEngineType()
    {
        bool ok = GpuCounterParser.TryParseInstance(
            "pid_1234_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D", out int pid, out string engineType);

        Assert.True(ok);
        Assert.Equal(1234, pid);
        Assert.Equal("3D", engineType);
    }

    [Fact]
    public void TryParseInstance_VideoDecodeEngine_ParsesEngineType()
    {
        bool ok = GpuCounterParser.TryParseInstance(
            "pid_42_luid_0x00000000_0x0000ABCD_phys_0_eng_1_engtype_VideoDecode", out int pid, out string engineType);

        Assert.True(ok);
        Assert.Equal(42, pid);
        Assert.Equal("VideoDecode", engineType);
    }

    [Fact]
    public void TryParseInstance_InvalidString_ReturnsFalse()
    {
        bool ok = GpuCounterParser.TryParseInstance("not_a_valid_instance", out int pid, out string engineType);

        Assert.False(ok);
        Assert.Equal(0, pid);
        Assert.Equal("", engineType);
    }

    [Fact]
    public void PerProcess_TakesMaxOverEnginesPerPid()
    {
        var values = new (string instance, double value)[]
        {
            ("pid_10_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D", 20),
            ("pid_10_luid_0x00000000_0x0000C6A1_phys_0_eng_5_engtype_Copy", 35),
        };

        var result = GpuCounterParser.PerProcess(values);

        Assert.Equal(35f, result[10]);
    }

    [Fact]
    public void Total3D_SumsOnly3DEnginesAndCaps()
    {
        var values = new (string instance, double value)[]
        {
            ("pid_10_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D", 60),
            ("pid_20_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D", 60),
            ("pid_20_luid_0x00000000_0x0000C6A1_phys_0_eng_5_engtype_Copy", 90),
        };

        float total = GpuCounterParser.Total3D(values);

        Assert.Equal(100f, total);
    }

    [Fact]
    public void MemoryPerProcess_SumsPerPid()
    {
        var values = new (string instance, double value)[]
        {
            ("pid_10_luid_0x00000000_0x0000C6A1_phys_0_eng_3_engtype_3D", 1024),
            ("pid_10_luid_0x00000000_0x0000C6A1_phys_0_eng_5_engtype_Copy", 2048),
        };

        var result = GpuCounterParser.MemoryPerProcess(values);

        Assert.Equal(3072L, result[10]);
    }

    [Fact]
    public void MemoryPerProcess_ParsesInstancesWithoutEngineType()
    {
        var values = new (string instance, double value)[]
        {
            ("pid_1234_luid_0x00000000_0x0000C6A1_phys_0", 1e9),
            ("pid_1234_luid_0x00000000_0x0000C6A1_phys_1", 5e8),
        };

        var result = GpuCounterParser.MemoryPerProcess(values);

        Assert.Equal(1_500_000_000L, result[1234]);
    }

    [Fact]
    public void TryParsePid_ValidInstance_ReturnsPid()
    {
        bool ok = GpuCounterParser.TryParsePid("pid_1234_luid_0x00000000_0x0000C6A1_phys_0", out int pid);

        Assert.True(ok);
        Assert.Equal(1234, pid);
    }

    [Fact]
    public void TryParsePid_MissingPidPrefix_ReturnsFalse()
    {
        bool ok = GpuCounterParser.TryParsePid("luid_0x1_phys_0", out int pid);

        Assert.False(ok);
        Assert.Equal(0, pid);
    }

    [Fact]
    public void TryParsePid_NonNumericPid_ReturnsFalse()
    {
        bool ok = GpuCounterParser.TryParsePid("pid_x_phys_0", out int pid);

        Assert.False(ok);
        Assert.Equal(0, pid);
    }
}
