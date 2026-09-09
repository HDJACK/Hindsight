namespace Hindsight.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TempFile_IsDeletedOnDispose()
    {
        string p;
        using (var t = new TempFile()) { p = t.Path; File.WriteAllText(p, "x"); }
        Assert.False(File.Exists(p));
    }
}
