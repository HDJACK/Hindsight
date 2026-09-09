namespace Hindsight.Core.Storage;

/// <summary>Shared helpers for the JSON Lines files the stores in this namespace append to.</summary>
internal static class JsonLinesFile
{
    /// <summary>Reads all lines while another process may hold the file open for appending.</summary>
    public static IEnumerable<string> ReadLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        while (r.ReadLine() is { } line) yield return line;
    }
}
