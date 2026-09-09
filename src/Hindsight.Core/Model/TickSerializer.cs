using System.Buffers.Binary;

namespace Hindsight.Core.Model;

/// <summary>Fixed-size binary layout of a <see cref="Tick"/>, shared by the ring buffer and recording files.</summary>
public static class TickSerializer
{
    public const int HeaderSize = 80;
    public const int SampleSize = 96;
    public const int RecordSize = HeaderSize + Tick.MaxSamples * SampleSize;

    /// <summary>Fixed size of the v1/v2 (legacy) 48/48 record layout, kept for reading old recordings.</summary>
    public const int LegacyRecordSize = 48 + Tick.MaxSamples * 48;

    public static void Write(Tick t, Span<byte> dst)
    {
        if (dst.Length < RecordSize) throw new ArgumentException("buffer too small", nameof(dst));
        dst = dst[..RecordSize];
        dst.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(dst[0..], t.UnixTime);
        BinaryPrimitives.WriteSingleLittleEndian(dst[8..], t.CpuTotalPercent);
        BinaryPrimitives.WriteInt64LittleEndian(dst[12..], t.DiskBytes);
        BinaryPrimitives.WriteInt64LittleEndian(dst[20..], t.NetBytes);
        BinaryPrimitives.WriteInt64LittleEndian(dst[28..], t.RamAvailableBytes);
        BinaryPrimitives.WriteInt32LittleEndian(dst[36..], t.ProcessCount);
        BinaryPrimitives.WriteInt32LittleEndian(dst[40..], t.LostEvents);
        dst[44] = (byte)t.Flags;
        int n = Math.Min(t.Samples.Length, Tick.MaxSamples);
        dst[45] = (byte)n;
        dst[46] = t.IntervalSeconds == 0 ? (byte)1 : t.IntervalSeconds;
        dst[47] = unchecked((byte)t.BatteryPercent);
        BinaryPrimitives.WriteSingleLittleEndian(dst[48..], t.GpuTotalPercent);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[52..], t.CpuMhz);
        BinaryPrimitives.WriteSingleLittleEndian(dst[56..], t.DiskQueue);
        BinaryPrimitives.WriteSingleLittleEndian(dst[60..], t.DiskLatencyMs);
        BinaryPrimitives.WriteSingleLittleEndian(dst[64..], t.CommitPercent);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[68..], t.HardFaultsPerSec);
        BinaryPrimitives.WriteInt32LittleEndian(dst[72..], t.ForegroundPid);
        for (int i = 0; i < n; i++)
        {
            var s = dst.Slice(HeaderSize + i * SampleSize, SampleSize);
            ref readonly var p = ref t.Samples[i];
            BinaryPrimitives.WriteInt32LittleEndian(s[0..], p.Pid);
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], p.IdentityId);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], p.CpuPercent);
            BinaryPrimitives.WriteInt64LittleEndian(s[12..], p.ReadBytes);
            BinaryPrimitives.WriteInt64LittleEndian(s[20..], p.WriteBytes);
            BinaryPrimitives.WriteInt64LittleEndian(s[28..], p.NetBytes);
            BinaryPrimitives.WriteInt64LittleEndian(s[36..], p.WorkingSet);
            s[44] = (byte)p.Flags;
            BinaryPrimitives.WriteSingleLittleEndian(s[48..], p.GpuPercent);
            BinaryPrimitives.WriteInt32LittleEndian(s[52..], p.HandleCount);
            BinaryPrimitives.WriteInt64LittleEndian(s[56..], p.GpuMemoryBytes);
            BinaryPrimitives.WriteInt64LittleEndian(s[64..], p.PrivateBytes);
            BinaryPrimitives.WriteInt32LittleEndian(s[72..], p.ThreadCount);
            BinaryPrimitives.WriteInt32LittleEndian(s[76..], p.HardFaults);
            BinaryPrimitives.WriteInt64LittleEndian(s[80..], p.DiskReadBytes);
            BinaryPrimitives.WriteInt64LittleEndian(s[88..], p.DiskWriteBytes);
        }
    }

    public static Tick Read(ReadOnlySpan<byte> src)
    {
        if (src.Length < RecordSize) throw new ArgumentException("buffer too small", nameof(src));
        var t = new Tick
        {
            UnixTime = BinaryPrimitives.ReadInt64LittleEndian(src[0..]),
            CpuTotalPercent = BinaryPrimitives.ReadSingleLittleEndian(src[8..]),
            DiskBytes = BinaryPrimitives.ReadInt64LittleEndian(src[12..]),
            NetBytes = BinaryPrimitives.ReadInt64LittleEndian(src[20..]),
            RamAvailableBytes = BinaryPrimitives.ReadInt64LittleEndian(src[28..]),
            ProcessCount = BinaryPrimitives.ReadInt32LittleEndian(src[36..]),
            LostEvents = BinaryPrimitives.ReadInt32LittleEndian(src[40..]),
            Flags = (TickFlags)src[44],
            IntervalSeconds = src[46] == 0 ? (byte)1 : src[46],
            BatteryPercent = unchecked((sbyte)src[47]),
            GpuTotalPercent = BinaryPrimitives.ReadSingleLittleEndian(src[48..]),
            CpuMhz = BinaryPrimitives.ReadUInt16LittleEndian(src[52..]),
            DiskQueue = BinaryPrimitives.ReadSingleLittleEndian(src[56..]),
            DiskLatencyMs = BinaryPrimitives.ReadSingleLittleEndian(src[60..]),
            CommitPercent = BinaryPrimitives.ReadSingleLittleEndian(src[64..]),
            HardFaultsPerSec = BinaryPrimitives.ReadUInt32LittleEndian(src[68..]),
            ForegroundPid = BinaryPrimitives.ReadInt32LittleEndian(src[72..]),
        };
        int n = Math.Min(src[45], (byte)Tick.MaxSamples);
        t.Samples = new ProcessSample[n];
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(HeaderSize + i * SampleSize, SampleSize);
            t.Samples[i] = new ProcessSample
            {
                Pid = BinaryPrimitives.ReadInt32LittleEndian(s[0..]),
                IdentityId = BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
                CpuPercent = BinaryPrimitives.ReadSingleLittleEndian(s[8..]),
                ReadBytes = BinaryPrimitives.ReadInt64LittleEndian(s[12..]),
                WriteBytes = BinaryPrimitives.ReadInt64LittleEndian(s[20..]),
                NetBytes = BinaryPrimitives.ReadInt64LittleEndian(s[28..]),
                WorkingSet = BinaryPrimitives.ReadInt64LittleEndian(s[36..]),
                Flags = (SampleFlags)s[44],
                GpuPercent = BinaryPrimitives.ReadSingleLittleEndian(s[48..]),
                HandleCount = BinaryPrimitives.ReadInt32LittleEndian(s[52..]),
                GpuMemoryBytes = BinaryPrimitives.ReadInt64LittleEndian(s[56..]),
                PrivateBytes = BinaryPrimitives.ReadInt64LittleEndian(s[64..]),
                ThreadCount = BinaryPrimitives.ReadInt32LittleEndian(s[72..]),
                HardFaults = BinaryPrimitives.ReadInt32LittleEndian(s[76..]),
                DiskReadBytes = BinaryPrimitives.ReadInt64LittleEndian(s[80..]),
                DiskWriteBytes = BinaryPrimitives.ReadInt64LittleEndian(s[88..]),
            };
        }
        return t;
    }

    /// <summary>Reads the legacy (v1) 48-byte header / 48-byte sample layout used by pre-v3 recordings.
    /// New fields introduced in v3 are left at their defaults.</summary>
    public static Tick ReadV2(ReadOnlySpan<byte> src)
    {
        const int legacyHeaderSize = 48;
        const int legacySampleSize = 48;
        if (src.Length < LegacyRecordSize) throw new ArgumentException("buffer too small", nameof(src));
        var t = new Tick
        {
            UnixTime = BinaryPrimitives.ReadInt64LittleEndian(src[0..]),
            CpuTotalPercent = BinaryPrimitives.ReadSingleLittleEndian(src[8..]),
            DiskBytes = BinaryPrimitives.ReadInt64LittleEndian(src[12..]),
            NetBytes = BinaryPrimitives.ReadInt64LittleEndian(src[20..]),
            RamAvailableBytes = BinaryPrimitives.ReadInt64LittleEndian(src[28..]),
            ProcessCount = BinaryPrimitives.ReadInt32LittleEndian(src[36..]),
            LostEvents = BinaryPrimitives.ReadInt32LittleEndian(src[40..]),
            Flags = (TickFlags)src[44],
            IntervalSeconds = src[46] == 0 ? (byte)1 : src[46],
        };
        int n = Math.Min(src[45], (byte)Tick.MaxSamples);
        t.Samples = new ProcessSample[n];
        for (int i = 0; i < n; i++)
        {
            var s = src.Slice(legacyHeaderSize + i * legacySampleSize, legacySampleSize);
            t.Samples[i] = new ProcessSample
            {
                Pid = BinaryPrimitives.ReadInt32LittleEndian(s[0..]),
                IdentityId = BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
                CpuPercent = BinaryPrimitives.ReadSingleLittleEndian(s[8..]),
                ReadBytes = BinaryPrimitives.ReadInt64LittleEndian(s[12..]),
                WriteBytes = BinaryPrimitives.ReadInt64LittleEndian(s[20..]),
                NetBytes = BinaryPrimitives.ReadInt64LittleEndian(s[28..]),
                WorkingSet = BinaryPrimitives.ReadInt64LittleEndian(s[36..]),
                Flags = (SampleFlags)s[44],
            };
        }
        return t;
    }
}
