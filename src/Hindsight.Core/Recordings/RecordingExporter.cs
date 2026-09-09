using System.Globalization;
using System.Text.Json;
using Hindsight.Core.Model;

namespace Hindsight.Core.Recordings;

/// <summary>Writes a recording out as CSV or JSON for use in other tools.</summary>
public static class RecordingExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void TicksCsv(Recording r, TextWriter w)
    {
        w.WriteLine("time_utc,unix,cpu_percent,disk_bytes,net_bytes,ram_available_bytes,process_count,flags,top_process,gpu_total_percent,cpu_mhz,disk_latency_ms,commit_percent,battery_percent,foreground_pid");
        foreach (var t in r.Ticks)
        {
            string top = t.Samples.Length > 0 ? r.Lookup(t.Samples[0].IdentityId)?.Name ?? $"PID {t.Samples[0].Pid}" : "";
            w.WriteLine(string.Join(",",
                DateTimeOffset.FromUnixTimeSeconds(t.UnixTime).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv),
                t.UnixTime.ToString(Inv), t.CpuTotalPercent.ToString(Inv), t.DiskBytes.ToString(Inv), t.NetBytes.ToString(Inv),
                t.RamAvailableBytes.ToString(Inv), t.ProcessCount.ToString(Inv), Q(FlagText(t.Flags)), Q(top),
                t.GpuTotalPercent.ToString("0.##", Inv), t.CpuMhz.ToString(Inv), t.DiskLatencyMs.ToString("0.##", Inv),
                t.CommitPercent.ToString("0.##", Inv), t.BatteryPercent.ToString(Inv), t.ForegroundPid.ToString(Inv)));
        }
    }

    public static void ProcessesCsv(Recording r, TextWriter w)
    {
        w.WriteLine("process,pid,cpu_seconds,read_bytes,write_bytes,net_bytes,peak_working_set,seconds,first_seen_unix,last_seen_unix,command_line,gpu_percent_max,disk_read_bytes,disk_write_bytes,peak_private_bytes");
        foreach (var p in RecordingAggregates.PerProcess(r, long.MinValue, long.MaxValue))
        {
            string cmd = r.Lookup(p.IdentityId)?.CommandLine ?? "";
            w.WriteLine(string.Join(",", Q(p.Name), p.Pid.ToString(Inv), p.CpuSeconds.ToString("0.###", Inv), p.ReadBytes.ToString(Inv),
                p.WriteBytes.ToString(Inv), p.NetBytes.ToString(Inv), p.PeakWorkingSet.ToString(Inv), p.Seconds.ToString(Inv),
                p.FirstSeen.ToString(Inv), p.LastSeen.ToString(Inv), Q(cmd),
                p.GpuPercentMax.ToString("0.##", Inv), p.DiskReadBytes.ToString(Inv), p.DiskWriteBytes.ToString(Inv), p.PeakPrivateBytes.ToString(Inv)));
        }
    }

    public static void Json(Recording r, Stream stream)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WritePropertyName("meta"); JsonSerializer.Serialize(w, r.Meta);
        w.WritePropertyName("ticks"); w.WriteStartArray();
        foreach (var t in r.Ticks)
        {
            w.WriteStartObject();
            w.WriteNumber("unix", t.UnixTime);
            w.WriteNumber("cpuPercent", t.CpuTotalPercent);
            w.WriteNumber("diskBytes", t.DiskBytes);
            w.WriteNumber("netBytes", t.NetBytes);
            w.WriteNumber("ramAvailableBytes", t.RamAvailableBytes);
            w.WriteNumber("processCount", t.ProcessCount);
            w.WriteNumber("intervalSeconds", t.IntervalSeconds);
            w.WriteString("flags", FlagText(t.Flags));
            w.WriteNumber("gpuTotalPercent", t.GpuTotalPercent);
            w.WriteNumber("cpuMhz", t.CpuMhz);
            w.WriteNumber("diskLatencyMs", t.DiskLatencyMs);
            w.WriteNumber("commitPercent", t.CommitPercent);
            w.WriteNumber("batteryPercent", t.BatteryPercent);
            w.WriteNumber("foregroundPid", t.ForegroundPid);
            w.WritePropertyName("samples"); w.WriteStartArray();
            foreach (var s in t.Samples)
            {
                var id = r.Lookup(s.IdentityId);
                w.WriteStartObject();
                w.WriteNumber("pid", s.Pid);
                w.WriteString("name", id?.Name ?? $"PID {s.Pid}");
                w.WriteNumber("cpuPercent", s.CpuPercent);
                w.WriteNumber("readBytes", s.ReadBytes);
                w.WriteNumber("writeBytes", s.WriteBytes);
                w.WriteNumber("netBytes", s.NetBytes);
                w.WriteNumber("workingSet", s.WorkingSet);
                w.WriteString("flags", FlagText(s.Flags));
                w.WriteNumber("gpuPercent", s.GpuPercent);
                w.WriteNumber("gpuMemoryBytes", s.GpuMemoryBytes);
                w.WriteNumber("privateBytes", s.PrivateBytes);
                w.WriteNumber("handleCount", s.HandleCount);
                w.WriteNumber("threadCount", s.ThreadCount);
                w.WriteNumber("hardFaults", s.HardFaults);
                w.WriteNumber("diskReadBytes", s.DiskReadBytes);
                w.WriteNumber("diskWriteBytes", s.DiskWriteBytes);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WritePropertyName("identities"); JsonSerializer.Serialize(w, r.Identities);
        w.WritePropertyName("markers"); JsonSerializer.Serialize(w, r.Markers);
        w.WriteEndObject();
    }

    private static string FlagText(TickFlags f) => f == TickFlags.None ? "" : f.ToString().Replace(", ", "|");

    private static string FlagText(SampleFlags f) => f == SampleFlags.None ? "" : f.ToString().Replace(", ", "|");

    private static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
