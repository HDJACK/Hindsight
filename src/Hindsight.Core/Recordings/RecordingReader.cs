using System.IO.Compression;
using System.Text.Json;
using Hindsight.Core.Model;

namespace Hindsight.Core.Recordings;

/// <summary>Loads a recording file, including the older formats still supported.</summary>
public static class RecordingReader
{
    public static Recording Open(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var meta = ReadMeta(zip);
            var ticks = new List<Tick>(meta.TickCount);
            var ticksEntry = zip.GetEntry("ticks.bin") ?? throw new InvalidDataException("ticks.bin missing");
            int recordSize = meta.FormatVersion == 1 ? TickSerializer.LegacyRecordSize : TickSerializer.RecordSize;
            using (var s = ticksEntry.Open())
            {
                var buf = new byte[recordSize];
                for (int i = 0; i < meta.TickCount; i++)
                {
                    s.ReadExactly(buf);
                    ticks.Add(meta.FormatVersion == 1 ? TickSerializer.ReadV2(buf) : TickSerializer.Read(buf));
                }
            }
            // Identity ids are the line index, so blank lines must be kept as placeholders rather than skipped.
            var ids = ReadAllLines(zip, "identities.jsonl")
                .Select((line, i) => TryJson<ProcessIdentity>(line) ?? ProcessIdentity.Placeholder(-1, i)).ToList();
            var markers = ReadLines(zip, "markers.jsonl").Select(l => TryJson<Marker>(l)).Where(m => m is not null).Select(m => m!).ToList();
            var context = ReadLines(zip, "context.jsonl").Select(l => TryJson<ContextEntry>(l)).Where(c => c is not null).Select(c => c!).ToList();
            return new Recording(meta, ticks, ids, markers, context);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a valid recording: {ex.Message}", ex);
        }
    }

    internal static RecordingMeta ReadMeta(ZipArchive zip)
    {
        var entry = zip.GetEntry("meta.json") ?? throw new InvalidDataException("meta.json missing");
        using var r = new StreamReader(entry.Open());
        var meta = JsonSerializer.Deserialize<RecordingMeta>(r.ReadToEnd()) ?? throw new InvalidDataException("meta.json empty");
        if (meta.FormatVersion is < 1 or > RecordingMeta.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported recording format version {meta.FormatVersion}");
        return meta;
    }

    private static IEnumerable<string> ReadLines(ZipArchive zip, string name)
    {
        foreach (var line in ReadAllLines(zip, name))
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }

    /// <summary>Yields every line including blanks, so a caller that keys by line index sees a stable id space.</summary>
    private static IEnumerable<string> ReadAllLines(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) yield break;
        using var r = new StreamReader(entry.Open());
        while (r.ReadLine() is { } line)
            yield return line;
    }

    private static T? TryJson<T>(string line) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(line); } catch (JsonException) { return null; }
    }
}
