using Hindsight.Core.Model;
using Hindsight.Core.Storage;

namespace Hindsight.Core.Tests;

public class IdentityStoreTests
{
    private static readonly ProcessIdentity A = new(100, 5000, "a.exe", @"C:\a.exe", "a.exe --x", 1);
    private static readonly ProcessIdentity B = new(200, 6000, "b.exe", @"C:\b.exe", "", 100);

    [Fact]
    public void Intern_SameKey_ReturnsSameId()
    {
        using var s = new IdentityStore(null);
        int id1 = s.Intern(A);
        int id2 = s.Intern(A with { CommandLine = "other" });
        Assert.Equal(id1, id2);
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Lookup_ReturnsInterned()
    {
        using var s = new IdentityStore(null);
        int a = s.Intern(A); int b = s.Intern(B);
        Assert.Equal("a.exe", s.Lookup(a)!.Name);
        Assert.Equal("b.exe", s.Lookup(b)!.Name);
        Assert.Null(s.Lookup(99));
        Assert.Null(s.Lookup(-1));
    }

    [Fact]
    public void Reopen_PreservesIdsAndContent()
    {
        using var f = new TempFile();
        int a, b;
        using (var s = new IdentityStore(f.Path)) { a = s.Intern(A); b = s.Intern(B); }
        using (var s = new IdentityStore(f.Path))
        {
            Assert.Equal(2, s.Count);
            Assert.Equal("a.exe --x", s.Lookup(a)!.CommandLine);
            Assert.Equal(100, s.Lookup(b)!.ParentPid);
            Assert.Equal(a, s.Intern(A));
        }
    }

    [Fact]
    public void CorruptLine_BecomesPlaceholder_KeepingIdsStable()
    {
        using var f = new TempFile();
        using (var s = new IdentityStore(f.Path)) { s.Intern(A); }
        File.AppendAllText(f.Path, "{ this is not json\n");
        using (var s = new IdentityStore(f.Path)) { s.Intern(B); }
        using var s2 = new IdentityStore(f.Path);
        Assert.Equal(3, s2.Count);
        Assert.Equal("a.exe", s2.Lookup(0)!.Name);
        Assert.StartsWith("PID ", s2.Lookup(1)!.Name);
        Assert.Equal("b.exe", s2.Lookup(2)!.Name);
    }

    [Fact]
    public void Update_RewritesLine()
    {
        using var f = new TempFile();
        int a, b;
        using (var s = new IdentityStore(f.Path))
        {
            a = s.Intern(A);
            b = s.Intern(B);
            s.Update(B with { Services = "x" });
            Assert.Equal("x", s.Lookup(b)!.Services);
            Assert.Equal(2, s.Count);
        }
        using (var s = new IdentityStore(f.Path))
        {
            Assert.Equal(2, s.Count);
            Assert.Equal("x", s.Lookup(b)!.Services);
            Assert.Equal("a.exe", s.Lookup(a)!.Name);
            Assert.Equal(b, s.Intern(B));
        }
        string before = File.ReadAllText(f.Path);
        using (var s = new IdentityStore(f.Path))
        {
            s.Update(new ProcessIdentity(999, 999, "z.exe", "", "", 0, Services: "y"));
            Assert.Equal(2, s.Count);
        }
        Assert.Equal(before, File.ReadAllText(f.Path));
    }

    [Fact]
    public void UpdateMany_RewritesOnce_AndSkipsUnknown()
    {
        using var f = new TempFile();
        int a, b, c;
        var C = new ProcessIdentity(300, 7000, "c.exe", @"C:\c.exe", "", 100);
        using (var s = new IdentityStore(f.Path))
        {
            a = s.Intern(A);
            b = s.Intern(B);
            c = s.Intern(C);
            s.UpdateMany(new[]
            {
                A with { Services = "svc-a" },
                B with { Services = "svc-b" },
                new ProcessIdentity(999, 999, "z.exe", "", "", 0, Services: "y"),
            });
            Assert.Equal("svc-a", s.Lookup(a)!.Services);
            Assert.Equal("svc-b", s.Lookup(b)!.Services);
            Assert.Equal(3, s.Count);
        }
        using (var s = new IdentityStore(f.Path))
        {
            Assert.Equal(3, s.Count);
            Assert.Equal("svc-a", s.Lookup(a)!.Services);
            Assert.Equal("svc-b", s.Lookup(b)!.Services);
            Assert.Equal("c.exe", s.Lookup(c)!.Name);
        }
        Assert.Equal(3, File.ReadAllLines(f.Path).Length);
    }

    [Fact]
    public void SecondStore_CanReadWhileFirstIsOpenForWriting()
    {
        using var f = new TempFile();
        using var writer = new IdentityStore(f.Path);
        writer.Intern(A);
        using var reader = new IdentityStore(f.Path);
        Assert.Equal("a.exe", reader.Lookup(0)!.Name);
    }
}
