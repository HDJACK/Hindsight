using System.Text.Json;
using Hindsight.Core.Model;
using Hindsight.Core.Recordings;

namespace Hindsight.Core.Tests;

public class RecordingExporterTests
{
    private static Recording Make()
    {
        var ids = new[] { new ProcessIdentity(1, 1, "a \"quoted\".exe", "", "x, y", 0) };
        var ticks = new List<Tick>
        {
            new() { UnixTime = 1_700_000_000, CpuTotalPercent = 12.5f, DiskBytes = 1024, NetBytes = 10, RamAvailableBytes = 5, ProcessCount = 3, Flags = TickFlags.Spike, IntervalSeconds = 1,
                    GpuTotalPercent = 9, CpuMhz = 4000,
                    Samples = new[]
                    {
                        new ProcessSample { Pid = 1, IdentityId = 0, CpuPercent = 7.25f, ReadBytes = 1024, GpuPercent = 3.5f, DiskReadBytes = 7 },
                        new ProcessSample { Pid = 2, IdentityId = 1, CpuPercent = 1f, Flags = SampleFlags.ShortLived | SampleFlags.Estimated },
                    } },
        };
        return new Recording(new RecordingMeta(1, "exp", "n", 1_700_000_000, 1_700_000_000, 1, 2, "m", 1, ""), ticks, ids, new[] { new Marker(1_700_000_000, MarkerKind.Manual, "hi") });
    }

    [Fact]
    public void TicksCsv_HasHeaderAndQuotedValues()
    {
        var sw = new StringWriter();
        RecordingExporter.TicksCsv(Make(), sw);
        var lines = sw.ToString().TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("time_utc,unix,cpu_percent,disk_bytes,net_bytes,ram_available_bytes,process_count,flags,top_process,gpu_total_percent,cpu_mhz,disk_latency_ms,commit_percent,battery_percent,foreground_pid", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains(",1700000000,12.5,1024,10,5,3,\"Spike\",\"a \"\"quoted\"\".exe\",9,4000,0,0,-1,0", lines[1]);
        Assert.StartsWith("2023-11-14T22:13:20Z", lines[1]);
    }

    [Fact]
    public void ProcessesCsv_OneRowPerProcess()
    {
        var sw = new StringWriter();
        RecordingExporter.ProcessesCsv(Make(), sw);
        var lines = sw.ToString().TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("process,pid,cpu_seconds,read_bytes,write_bytes,net_bytes,peak_working_set,seconds,first_seen_unix,last_seen_unix,command_line,gpu_percent_max,disk_read_bytes,disk_write_bytes,peak_private_bytes", lines[0]);
        Assert.Equal("\"a \"\"quoted\"\".exe\",1,0.145,1024,0,0,0,1,1700000000,1700000000,\"x, y\",3.5,7,0,0", lines[1]);
    }

    [Fact]
    public void Json_ContainsMetaTicksMarkersAndNames()
    {
        using var ms = new MemoryStream();
        RecordingExporter.Json(Make(), ms);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var root = doc.RootElement;
        Assert.Equal("exp", root.GetProperty("meta").GetProperty("Name").GetString());
        var tick = root.GetProperty("ticks")[0];
        Assert.Equal(12.5, tick.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(9, tick.GetProperty("gpuTotalPercent").GetDouble());
        Assert.Equal(4000, tick.GetProperty("cpuMhz").GetInt32());
        Assert.Equal("a \"quoted\".exe", tick.GetProperty("samples")[0].GetProperty("name").GetString());
        Assert.Equal(3.5, tick.GetProperty("samples")[0].GetProperty("gpuPercent").GetDouble());
        Assert.Equal(7, tick.GetProperty("samples")[0].GetProperty("diskReadBytes").GetInt64());
        Assert.Equal("", tick.GetProperty("samples")[0].GetProperty("flags").GetString());
        Assert.Equal("ShortLived|Estimated", tick.GetProperty("samples")[1].GetProperty("flags").GetString());
        Assert.Equal("hi", root.GetProperty("markers")[0].GetProperty("Text").GetString());
    }
}
