namespace Hindsight.Core.Tests;

public sealed class TempFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "hindsight-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
    }
}
