using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class RingBufferTests
{
    private static Tick T(long unix) => new() { UnixTime = unix, CpuTotalPercent = unix % 100 };

    [Fact]
    public void Write_ThenReadAll_IsChronological()
    {
        using var f = new TempFile();
        using var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        rb.Write(T(1)); rb.Write(T(2)); rb.Write(T(3));
        Assert.Equal(3, rb.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, rb.ReadAll().Select(t => t.UnixTime));
        Assert.Equal(3, rb.Latest!.UnixTime);
    }

    [Fact]
    public void WrapAround_KeepsNewestCapacityTicks()
    {
        using var f = new TempFile();
        using var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        for (int i = 0; i < rb.Capacity + 5; i++) rb.Write(T(i));
        Assert.Equal(rb.Capacity, rb.Count);
        Assert.Equal(5, rb.ReadAt(0).UnixTime);
        Assert.Equal(rb.Capacity + 4, rb.Latest!.UnixTime);
    }

    [Fact]
    public void Reopen_PreservesContent()
    {
        using var f = new TempFile();
        using (var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity)) { rb.Write(T(10)); rb.Write(T(11)); }
        using (var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity))
        {
            Assert.Equal(2, rb.Count);
            Assert.Equal(11, rb.Latest!.UnixTime);
            rb.Write(T(12));
            Assert.Equal(new long[] { 10, 11, 12 }, rb.ReadAll().Select(t => t.UnixTime));
        }
    }

    [Fact]
    public void CorruptMagic_StartsFresh()
    {
        using var f = new TempFile();
        using (var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity)) rb.Write(T(1));
        using (var fs = File.OpenWrite(f.Path)) { fs.Write(new byte[] { 0, 0, 0, 0 }); }
        using var rb2 = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        Assert.Equal(0, rb2.Count);
        Assert.Null(rb2.Latest);
    }

    [Fact]
    public void ReadAt_OutOfRange_Throws()
    {
        using var f = new TempFile();
        using var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => rb.ReadAt(0));
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        using var f = new TempFile();
        var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        rb.Dispose();
        rb.Dispose();
    }

    [Fact]
    public void Open_WithCustomCapacity_WrapsAtThatCapacity()
    {
        using var f = new TempFile();
        using var rb = RingBuffer.Open(f.Path, 10);
        for (int i = 0; i < 13; i++) rb.Write(T(i));
        Assert.Equal(10, rb.Capacity);
        Assert.Equal(10, rb.Count);
        Assert.Equal(3, rb.ReadAt(0).UnixTime);
    }

    [Fact]
    public void Open_CapacityMismatch_RecreatesFile()
    {
        using var f = new TempFile();
        using (var rb = RingBuffer.Open(f.Path, 10)) rb.Write(T(1));
        using var rb2 = RingBuffer.Open(f.Path, 20);
        Assert.Equal(0, rb2.Count);
        Assert.Equal(20, rb2.Capacity);
    }

    [Fact]
    public void Open_VersionOneFile_IsRecreated()
    {
        using var f = new TempFile();
        // File layout: 16-byte header (magic, version 1, writeIndex, count) + 1800 records
        var bytes = new byte[16 + 1800L * TickSerializer.RecordSize];
        BitConverter.GetBytes(0x31525348u).CopyTo(bytes, 0);
        BitConverter.GetBytes(1).CopyTo(bytes, 4);
        BitConverter.GetBytes(5).CopyTo(bytes, 12);
        File.WriteAllBytes(f.Path, bytes);
        using var rb = RingBuffer.Open(f.Path, 1800);
        Assert.Equal(0, rb.Count);
    }

    [Fact]
    public void ReadAfterDispose_ThrowsObjectDisposed()
    {
        using var f = new TempFile();
        var rb = RingBuffer.Open(f.Path, RingBuffer.DefaultCapacity);
        rb.Dispose();
        Assert.Throws<ObjectDisposedException>(() => rb.ReadAll());
        Assert.Throws<ObjectDisposedException>(() => rb.Write(new Hindsight.Core.Model.Tick()));
    }

    [Fact]
    public void Open_AllowsAConcurrentReader()
    {
        using var f = new TempFile();
        using var writer = RingBuffer.Open(f.Path, 10);
        writer.Write(T(1)); writer.Write(T(2));
        using var reader = RingBuffer.OpenRead(f.Path, 10);
        Assert.Equal(2, reader.Count);
        Assert.Equal(2, reader.Latest!.UnixTime);
        writer.Write(T(3));
        reader.RefreshHeader();
        Assert.Equal(3, reader.Count);
        Assert.Equal(3, reader.Latest!.UnixTime);
        Assert.Throws<InvalidOperationException>(() => reader.Write(T(4)));
    }

    [Fact]
    public void OpenRead_OnMissingOrInvalidFile_Throws()
    {
        using var f = new TempFile();
        Assert.Throws<FileNotFoundException>(() => RingBuffer.OpenRead(f.Path, 10));
        File.WriteAllBytes(f.Path, new byte[64]);
        Assert.Throws<InvalidDataException>(() => RingBuffer.OpenRead(f.Path, 10));
    }
}
