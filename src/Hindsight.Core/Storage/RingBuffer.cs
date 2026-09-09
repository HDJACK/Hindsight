using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Hindsight.Core.Model;

namespace Hindsight.Core.Storage;

/// <summary>
/// File layout: 32 byte header (Magic u32, Version i32, Capacity i32, WriteIndex i32,
/// Count i32, reserved 12 bytes), then Capacity slots of TickSerializer.RecordSize each.
/// WriteIndex/Count are written after the slot, so a crash loses at most
/// the current tick.
/// </summary>
public sealed class RingBuffer : IDisposable
{
    public const int DefaultCapacity = 1800;
    private const int FileHeaderSize = 32;
    private const uint Magic = 0x31525348; // "HSR1"
    private const int Version = 3;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte[] _scratch = new byte[TickSerializer.RecordSize];
    private readonly object _lock = new();
    private int _writeIndex;
    private int _count;
    private bool _disposed;
    private readonly bool _readOnly;

    public int Capacity { get; }

    public int Count { get { lock (_lock) { ObjectDisposedException.ThrowIf(_disposed, this); return _count; } } }
    public Tick? Latest { get { lock (_lock) { ObjectDisposedException.ThrowIf(_disposed, this); return _count == 0 ? null : ReadAt(_count - 1); } } }

    /// <summary>Opens (or recreates) the ring for writing. Readers may open the same file concurrently via OpenRead.
    /// A short retry covers the moment a previous instance is still releasing its mapping.</summary>
    public static RingBuffer Open(string path, int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        long expected = FileHeaderSize + (long)capacity * TickSerializer.RecordSize;
        IOException? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                bool fresh = !IsValidFile(path, capacity, expected);
                if (fresh)
                {
                    if (File.Exists(path)) File.Delete(path);
                    using var create = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                    create.SetLength(expected);
                }
                var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                var mmf = MemoryMappedFile.CreateFromFile(fs, null, expected, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                var rb = new RingBuffer(mmf, capacity, expected, readOnly: false);
                if (fresh) rb.WriteHeader(); else rb.ReadHeader();
                return rb;
            }
            catch (IOException ex) when (ex is not FileNotFoundException && attempt < 4)
            {
                last = ex;
                Thread.Sleep(200);
            }
        }
        throw last ?? new IOException("Ring buffer could not be opened: " + path);
    }

    /// <summary>Read-only view of a ring another process is writing. Call RefreshHeader() to pick up new ticks.</summary>
    public static RingBuffer OpenRead(string path, int capacity = DefaultCapacity)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Ring buffer not found", path);
        long expected = FileHeaderSize + (long)capacity * TickSerializer.RecordSize;
        if (!IsValidFile(path, capacity, expected)) throw new InvalidDataException($"'{path}' is not a ring buffer with capacity {capacity}");
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var mmf = MemoryMappedFile.CreateFromFile(fs, null, expected, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        var rb = new RingBuffer(mmf, capacity, expected, readOnly: true);
        rb.ReadHeader();
        return rb;
    }

    /// <summary>Re-reads writeIndex/count from the file (for read-only views of a live ring).</summary>
    public void RefreshHeader()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int wi = _view.ReadInt32(12), c = _view.ReadInt32(16);
            if (wi >= 0 && wi < Capacity && c >= 0 && c <= Capacity) { _writeIndex = wi; _count = c; }
        }
    }

    private static bool IsValidFile(string path, int capacity, long expected)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expected) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> h = stackalloc byte[12];
        if (fs.Read(h) != 12) return false;
        return BinaryPrimitives.ReadUInt32LittleEndian(h) == Magic
            && BinaryPrimitives.ReadInt32LittleEndian(h[4..]) == Version
            && BinaryPrimitives.ReadInt32LittleEndian(h[8..]) == capacity;
    }

    private RingBuffer(MemoryMappedFile mmf, int capacity, long fileSize, bool readOnly)
    {
        _mmf = mmf;
        Capacity = capacity;
        _readOnly = readOnly;
        _view = mmf.CreateViewAccessor(0, fileSize, readOnly ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite);
    }

    private void WriteHeader()
    {
        _view.Write(0, Magic);
        _view.Write(4, Version);
        _view.Write(8, Capacity);
        _view.Write(12, _writeIndex);
        _view.Write(16, _count);
    }

    private void ReadHeader()
    {
        _writeIndex = _view.ReadInt32(12);
        _count = _view.ReadInt32(16);
        if (_writeIndex < 0 || _writeIndex >= Capacity || _count < 0 || _count > Capacity)
        {
            _writeIndex = 0; _count = 0;
            if (!_readOnly) WriteHeader();
        }
    }

    public void Write(Tick t)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readOnly) throw new InvalidOperationException("This ring buffer was opened read-only.");
            TickSerializer.Write(t, _scratch);
            _view.WriteArray(FileHeaderSize + (long)_writeIndex * TickSerializer.RecordSize, _scratch, 0, _scratch.Length);
            _writeIndex = (_writeIndex + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            _view.Write(12, _writeIndex);
            _view.Write(16, _count);
        }
    }

    public Tick ReadAt(int logicalIndex)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (logicalIndex < 0 || logicalIndex >= _count) throw new ArgumentOutOfRangeException(nameof(logicalIndex));
            int start = (_writeIndex - _count + Capacity) % Capacity;
            int slot = (start + logicalIndex) % Capacity;
            var buf = new byte[TickSerializer.RecordSize];
            _view.ReadArray(FileHeaderSize + (long)slot * TickSerializer.RecordSize, buf, 0, buf.Length);
            return TickSerializer.Read(buf);
        }
    }

    public List<Tick> ReadAll()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var list = new List<Tick>(_count);
            for (int i = 0; i < _count; i++) list.Add(ReadAt(i));
            return list;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _view.Flush();
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
