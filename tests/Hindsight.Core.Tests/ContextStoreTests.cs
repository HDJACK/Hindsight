using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class ContextStoreTests
{
    private static ContextEntry E(long minute, int pid, ContextKind k, string key, long v) => new(minute, pid, 0, k, key, v);

    [Fact]
    public void Append_Load_ForMinute_InRange_Trim()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "context.jsonl");
        using (var s = new ContextStore(path, 0))
            s.Append(new[] { E(1200, 5, ContextKind.File, "a", 3), E(1200, 5, ContextKind.Dns, "d", 1), E(1260, 5, ContextKind.File, "b", 9), E(1260, 6, ContextKind.File, "c", 1) });
        using (var s = new ContextStore(path, 0))
        {
            Assert.Equal(4, s.Count);
            var m = s.ForMinute(1200, 5);
            Assert.Equal(2, m.Count); Assert.Equal(ContextKind.Dns, m[0].Kind);     // Kind order Dns, Endpoint, File
            Assert.Equal(4, s.InRange(1230, 1290).Count);                            // minutes 1200 (contains 1230) and 1260: 2 + 2 entries
            s.Trim(1260);
            Assert.Equal(2, s.Count);
        }
        using (var s = new ContextStore(path, 0)) Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Load_DropsOldEntries_AndSkipsCorruptLines()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "context.jsonl");
        File.WriteAllLines(path, new[] { System.Text.Json.JsonSerializer.Serialize(E(600, 1, ContextKind.File, "old", 1)), "{not json", System.Text.Json.JsonSerializer.Serialize(E(1200, 1, ContextKind.File, "new", 1)) });
        using var s = new ContextStore(path, 1200);
        Assert.Equal(1, s.Count);
#pragma warning disable xUnit2013 // the intent is "one line survived", not a collection-size check
        Assert.Equal(1, File.ReadAllLines(path).Length);
#pragma warning restore xUnit2013
    }

    [Fact]
    public void Append_MergesDuplicateKeyWithinSameMinute()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "context.jsonl");
        using (var s = new ContextStore(path, 0))
        {
            s.Append(new[] { E(1200, 5, ContextKind.File, "a", 3) });
            s.Append(new[] { E(1200, 5, ContextKind.File, "a", 4) });
            var m = s.ForMinute(1200, 5);
            var e = Assert.Single(m);
            Assert.Equal(7, e.Value);
        }
        using (var s = new ContextStore(path, 0))
        {
            var m = s.ForMinute(1200, 5);
            var e = Assert.Single(m);
            Assert.Equal(7, e.Value);
        }
    }

    [Fact]
    public void NullPath_IsInMemory()
    {
        using var s = new ContextStore(null, 0);
        s.Append(new[] { E(0, 1, ContextKind.File, "x", 1) });
        Assert.Single(s.ForMinute(0, 1));
    }
}
