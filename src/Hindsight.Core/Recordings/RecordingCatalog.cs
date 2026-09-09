using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Hindsight.Core.Recordings;

/// <summary>One recording file in the catalog; <c>Meta</c> is null when the file could not be read.</summary>
public sealed record RecordingEntry(string Path, RecordingMeta? Meta, long SizeBytes, string? Error)
{
    public bool IsReadable => Meta is not null;
    public string DisplayName => Meta?.Name ?? "(unreadable)";
}

/// <summary>Lists, renames and deletes the recording files in a folder.</summary>
public sealed class RecordingCatalog
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string Folder { get; }

    public RecordingCatalog(string folder) => Folder = folder;

    public IReadOnlyList<RecordingEntry> List()
    {
        Directory.CreateDirectory(Folder);
        var entries = new List<RecordingEntry>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*" + RecordingWriter.Extension))
        {
            try { long size = new FileInfo(file).Length; entries.Add(new RecordingEntry(file, ReadMeta(file), size, null)); }
            catch (Exception ex)
            {
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { /* best effort */ }
                entries.Add(new RecordingEntry(file, null, size, ex.Message));
            }
        }
        return entries.OrderBy(e => e.IsReadable ? 0 : 1).ThenByDescending(e => e.Meta?.StartUnix ?? 0).ToList();
    }

    public static RecordingMeta ReadMeta(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return RecordingReader.ReadMeta(zip);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a valid recording: {ex.Message}", ex);
        }
    }

    public void Rename(string path, string newName)
    {
        var meta = ReadMeta(path) with { Name = newName };
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        zip.GetEntry("meta.json")?.Delete();
        using var w = new StreamWriter(zip.CreateEntry("meta.json").Open(), new UTF8Encoding(false));
        w.Write(JsonSerializer.Serialize(meta, Json));
    }

    public void Delete(string path) => File.Delete(path);
}
