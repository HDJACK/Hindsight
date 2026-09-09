using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hindsight.Core.Model;
using Hindsight.Core.Processes;

namespace Hindsight.Core.Recordings;

/// <summary>Collects ticks, markers and context, then writes them out as a .hsrec file.</summary>
public sealed class RecordingWriter
{
    public const string Extension = ".hsrec";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IIdentitySource _identities;
    private readonly int _intervalSeconds, _coreCount;
    private readonly List<Tick> _ticks = new();
    private readonly List<Marker> _markers = new();
    private readonly List<ContextEntry> _context = new();
    private readonly object _lock = new();

    public RecordingWriter(IIdentitySource identities, int intervalSeconds, int coreCount)
    {
        _identities = identities; _intervalSeconds = intervalSeconds; _coreCount = coreCount;
    }

    public int TickCount { get { lock (_lock) return _ticks.Count; } }

    public void Append(Tick tick) { lock (_lock) _ticks.Add(tick.Copy()); }

    public void AddMarkers(IEnumerable<Marker> markers) { lock (_lock) _markers.AddRange(markers); }

    public void AddContext(IEnumerable<ContextEntry> context) { lock (_lock) _context.AddRange(context); }

    public string Finish(string folder, string name, string notes)
    {
        List<Tick> ticks; List<Marker> markers; List<ContextEntry> context;
        lock (_lock) { ticks = _ticks.ToList(); markers = _markers.ToList(); context = _context.ToList(); }
        return SaveRange(folder, name, notes, ticks, markers, _identities, _intervalSeconds, _coreCount, context);
    }

    public static string SaveRange(string folder, string name, string notes, IReadOnlyList<Tick> ticks, IEnumerable<Marker> markers,
        IIdentitySource identities, int intervalSeconds, int coreCount, IEnumerable<ContextEntry>? context = null)
    {
        Directory.CreateDirectory(folder);
        long start = ticks.Count > 0 ? ticks[0].UnixTime : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long end = ticks.Count > 0 ? ticks[^1].UnixTime : start;

        // Remap identity ids to dense 0..n-1 in first-seen order; unknown ids become placeholders.
        var map = new Dictionary<int, int>();
        var list = new List<ProcessIdentity>();
        var copies = new List<Tick>(ticks.Count);
        foreach (var t in ticks)
        {
            var c = t.Copy();
            for (int i = 0; i < c.Samples.Length; i++)
            {
                ref var s = ref c.Samples[i];
                if (!map.TryGetValue(s.IdentityId, out int dense))
                {
                    dense = list.Count;
                    map[s.IdentityId] = dense;
                    list.Add(identities.Lookup(s.IdentityId) ?? ProcessIdentity.Placeholder(s.Pid, 0));
                }
                s.IdentityId = dense;
            }
            copies.Add(c);
        }

        var meta = new RecordingMeta(RecordingMeta.CurrentFormatVersion, name, notes, start, end, intervalSeconds, coreCount,
            Environment.MachineName, copies.Count, typeof(RecordingWriter).Assembly.GetName().Version?.ToString() ?? "");

        string path = UniquePath(folder, start, name);
        string tmp = path + ".tmp";
        WriteZip(tmp, meta, copies, list, markers, start, end, context ?? Array.Empty<ContextEntry>());
        try
        {
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException)
        {
            // Another writer claimed `path` between UniquePath and Move; pick a fresh name and retry once.
            path = UniquePath(folder, start, name);
            File.Move(tmp, path, overwrite: false);
        }
        return path;
    }

    private static void WriteZip(string path, RecordingMeta meta, List<Tick> copies, List<ProcessIdentity> list,
        IEnumerable<Marker> markers, long start, long end, IEnumerable<ContextEntry> context)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("meta.json").Open(), new UTF8Encoding(false)))
            w.Write(JsonSerializer.Serialize(meta, Json));
        using (var s = zip.CreateEntry("ticks.bin").Open())
        {
            var buf = new byte[TickSerializer.RecordSize];
            foreach (var t in copies) { TickSerializer.Write(t, buf); s.Write(buf, 0, buf.Length); }
        }
        using (var w = new StreamWriter(zip.CreateEntry("identities.jsonl").Open(), new UTF8Encoding(false)))
            foreach (var id in list) w.WriteLine(JsonSerializer.Serialize(id));
        using (var w = new StreamWriter(zip.CreateEntry("markers.jsonl").Open(), new UTF8Encoding(false)))
            foreach (var m in markers.Where(m => m.UnixTime >= start && m.UnixTime <= end).OrderBy(m => m.UnixTime))
                w.WriteLine(JsonSerializer.Serialize(m));
        long fromMinute = ContextEntry.MinuteOf(start);
        using (var w = new StreamWriter(zip.CreateEntry("context.jsonl").Open(), new UTF8Encoding(false)))
            foreach (var e in context.Where(e => e.Minute >= fromMinute && e.Minute <= end))
                w.WriteLine(JsonSerializer.Serialize(e));
    }

    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char ch in name.Trim())
        {
            sb.Append(invalid.Contains(ch) || ch == ' ' ? '_' : ch);
        }
        string s = sb.ToString();
        if (s.Length > 60) s = s[..60];
        return s.Length == 0 ? "recording" : s;
    }

    private static string UniquePath(string folder, long startUnix, string name)
    {
        string stamp = DateTimeOffset.FromUnixTimeSeconds(startUnix).ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss");
        string baseName = $"{stamp}_{SanitizeName(name)}";
        string path = Path.Combine(folder, baseName + Extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}_{i}{Extension}");
        return path;
    }
}
