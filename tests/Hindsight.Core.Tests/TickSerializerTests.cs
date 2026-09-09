using System.Buffers.Binary;
using Hindsight.Core.Model;

namespace Hindsight.Core.Tests;

public class TickSerializerTests
{
    private static Tick Sample() => new()
    {
        UnixTime = 1_700_000_000,
        CpuTotalPercent = 42.5f,
        DiskBytes = 123_456_789,
        NetBytes = 987,
        RamAvailableBytes = 8L << 30,
        ProcessCount = 250,
        LostEvents = 3,
        Flags = TickFlags.Spike | TickFlags.EtwActive | TickFlags.OnAc | TickFlags.UserIdle,
        GpuTotalPercent = 33.5f,
        CpuMhz = 4200,
        DiskQueue = 1.5f,
        DiskLatencyMs = 12.25f,
        CommitPercent = 61f,
        HardFaultsPerSec = 7,
        BatteryPercent = 88,
        ForegroundPid = 4242,
        Samples = new[]
        {
            new ProcessSample { Pid = 4, IdentityId = 1, CpuPercent = 10f, ReadBytes = 1, WriteBytes = 2, NetBytes = 3, WorkingSet = 4, Flags = SampleFlags.None },
            new ProcessSample
            {
                Pid = 999, IdentityId = 7, CpuPercent = 0.5f, ReadBytes = 5, WriteBytes = 6, NetBytes = 7, WorkingSet = 8, Flags = SampleFlags.ShortLived | SampleFlags.Estimated,
                GpuPercent = 45.5f, GpuMemoryBytes = 1L << 30, PrivateBytes = 123456789, HandleCount = 321, ThreadCount = 17, HardFaults = 9, DiskReadBytes = 55, DiskWriteBytes = 66,
            },
        }
    };

    [Fact]
    public void RecordSize_IsFixed()
    {
        Assert.Equal(3920, TickSerializer.RecordSize);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var t = Sample();
        var buf = new byte[TickSerializer.RecordSize];
        TickSerializer.Write(t, buf);
        var r = TickSerializer.Read(buf);

        Assert.Equal(t.UnixTime, r.UnixTime);
        Assert.Equal(t.CpuTotalPercent, r.CpuTotalPercent);
        Assert.Equal(t.DiskBytes, r.DiskBytes);
        Assert.Equal(t.NetBytes, r.NetBytes);
        Assert.Equal(t.RamAvailableBytes, r.RamAvailableBytes);
        Assert.Equal(t.ProcessCount, r.ProcessCount);
        Assert.Equal(t.LostEvents, r.LostEvents);
        Assert.Equal(t.Flags, r.Flags);
        Assert.Equal(t.GpuTotalPercent, r.GpuTotalPercent);
        Assert.Equal(t.CpuMhz, r.CpuMhz);
        Assert.Equal(t.DiskQueue, r.DiskQueue);
        Assert.Equal(t.DiskLatencyMs, r.DiskLatencyMs);
        Assert.Equal(t.CommitPercent, r.CommitPercent);
        Assert.Equal(t.HardFaultsPerSec, r.HardFaultsPerSec);
        Assert.Equal(t.BatteryPercent, r.BatteryPercent);
        Assert.Equal(t.ForegroundPid, r.ForegroundPid);
        Assert.Equal(2, r.Samples.Length);
        Assert.Equal(999, r.Samples[1].Pid);
        Assert.Equal(7, r.Samples[1].IdentityId);
        Assert.Equal(0.5f, r.Samples[1].CpuPercent);
        Assert.Equal(8, r.Samples[1].WorkingSet);
        Assert.Equal(SampleFlags.ShortLived | SampleFlags.Estimated, r.Samples[1].Flags);
        Assert.Equal(45.5f, r.Samples[1].GpuPercent);
        Assert.Equal(1L << 30, r.Samples[1].GpuMemoryBytes);
        Assert.Equal(123456789, r.Samples[1].PrivateBytes);
        Assert.Equal(321, r.Samples[1].HandleCount);
        Assert.Equal(17, r.Samples[1].ThreadCount);
        Assert.Equal(9, r.Samples[1].HardFaults);
        Assert.Equal(55, r.Samples[1].DiskReadBytes);
        Assert.Equal(66, r.Samples[1].DiskWriteBytes);
    }

    [Fact]
    public void ReadV2_ReadsLegacyLayout()
    {
        var buf = new byte[TickSerializer.LegacyRecordSize];
        BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(0), 5);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(8), 12.5f);
        buf[45] = 1;
        buf[46] = 2;
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(48), 7);
        BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(48 + 8), 1.5f);

        var t = TickSerializer.ReadV2(buf);

        Assert.Equal(5, t.UnixTime);
        Assert.Equal(12.5f, t.CpuTotalPercent);
        Assert.Equal(2, t.IntervalSeconds);
        Assert.Single(t.Samples);
        Assert.Equal(7, t.Samples[0].Pid);
        Assert.Equal(1.5f, t.Samples[0].CpuPercent);
        Assert.Equal(0, t.Samples[0].GpuPercent);
        Assert.Equal((sbyte)-1, t.BatteryPercent);
    }

    [Fact]
    public void Write_TruncatesToMaxSamples()
    {
        var t = Sample();
        t.Samples = Enumerable.Range(0, 60).Select(i => new ProcessSample { Pid = i }).ToArray();
        var buf = new byte[TickSerializer.RecordSize];
        TickSerializer.Write(t, buf);
        Assert.Equal(Tick.MaxSamples, TickSerializer.Read(buf).Samples.Length);
    }

    [Fact]
    public void Write_RejectsSmallBuffer()
    {
        Assert.Throws<ArgumentException>(() => TickSerializer.Write(Sample(), new byte[10]));
    }

    [Fact]
    public void RoundTrip_PreservesIntervalSeconds()
    {
        var t = Sample(); t.IntervalSeconds = 3;
        var buf = new byte[TickSerializer.RecordSize];
        TickSerializer.Write(t, buf);
        Assert.Equal(3, buf[46]);
        Assert.Equal(3, TickSerializer.Read(buf).IntervalSeconds);
    }

    [Fact]
    public void Read_ZeroInterval_IsNormalisedToOne()
    {
        var buf = new byte[TickSerializer.RecordSize];
        Assert.Equal(1, TickSerializer.Read(buf).IntervalSeconds);
    }
}
