namespace Hindsight.Core.Settings;

/// <summary>Bounds for every numeric setting, used both to reject bad input and to clamp a stored file back into range.</summary>
public static class SettingsValidator
{
    public const int MinBufferMinutes = 5, MaxBufferMinutes = 180;
    public const int MinInterval = 1, MaxInterval = 5;
    public const double MinCpu = 10, MaxCpu = 100;
    public const double MinIo = 1, MaxIo = 5000;
    public const double MinNet = 0.1, MaxNet = 5000;
    public const int MinBaseline = 30, MaxBaseline = 1800;
    public const double MinWeight = 0, MaxWeight = 10;
    public const double MinGpu = 10, MaxGpu = 100;
    public const double MinDiskLatency = 1, MaxDiskLatency = 5000;
    public const double MinCommit = 50, MaxCommit = 100;
    public const int MinIdle = 10, MaxIdle = 3600;
    public const int MinMinAnomaly = 1, MaxMinAnomaly = 60;

    public static IReadOnlyList<string> Validate(AppSettings s)
    {
        var e = new List<string>();
        Range(e, "Buffer length", s.BufferMinutes, MinBufferMinutes, MaxBufferMinutes, "min");
        Range(e, "Sample interval", s.SampleIntervalSeconds, MinInterval, MaxInterval, "s");
        if (string.IsNullOrWhiteSpace(s.DataFolder)) e.Add("Data folder must not be empty.");
        Range(e, "CPU spike threshold", s.CpuSpikePercent, MinCpu, MaxCpu, "%");
        Range(e, "File I/O spike threshold", s.FileIoSpikeMBps, MinIo, MaxIo, "MB/s");
        Range(e, "Network spike threshold", s.NetworkSpikeMBps, MinNet, MaxNet, "MB/s");
        Range(e, "Baseline window", s.BaselineWindowSeconds, MinBaseline, MaxBaseline, "s");
        foreach (var (name, v) in new[] { ("CPU weight", s.WeightCpu), ("I/O weight", s.WeightIo), ("Network weight", s.WeightNet), ("Memory weight", s.WeightMemory), ("GPU weight", s.WeightGpu) })
            Range(e, name, v, MinWeight, MaxWeight, "");
        Range(e, "GPU spike threshold", s.GpuSpikePercent, MinGpu, MaxGpu, "%");
        Range(e, "Disk latency spike threshold", s.DiskLatencySpikeMs, MinDiskLatency, MaxDiskLatency, "ms");
        Range(e, "Commit spike threshold", s.CommitSpikePercent, MinCommit, MaxCommit, "%");
        Range(e, "Idle after", s.IdleAfterSeconds, MinIdle, MaxIdle, "s");
        Range(e, "Minimum anomaly duration", s.MinAnomalySeconds, MinMinAnomaly, MaxMinAnomaly, "s");
        if (!HotkeyGesture.TryParse(s.MarkerHotkey, out _)) e.Add($"Marker hotkey '{s.MarkerHotkey}' is not a valid gesture (e.g. Ctrl+Alt+M).");
        return e;
    }

    public static AppSettings Clamp(AppSettings s) => s with
    {
        BufferMinutes = Math.Clamp(s.BufferMinutes, MinBufferMinutes, MaxBufferMinutes),
        SampleIntervalSeconds = Math.Clamp(s.SampleIntervalSeconds, MinInterval, MaxInterval),
        DataFolder = string.IsNullOrWhiteSpace(s.DataFolder) ? AppSettings.DefaultDataFolder : s.DataFolder,
        CpuSpikePercent = Math.Clamp(s.CpuSpikePercent, MinCpu, MaxCpu),
        FileIoSpikeMBps = Math.Clamp(s.FileIoSpikeMBps, MinIo, MaxIo),
        NetworkSpikeMBps = Math.Clamp(s.NetworkSpikeMBps, MinNet, MaxNet),
        BaselineWindowSeconds = Math.Clamp(s.BaselineWindowSeconds, MinBaseline, MaxBaseline),
        WeightCpu = Math.Clamp(s.WeightCpu, MinWeight, MaxWeight),
        WeightIo = Math.Clamp(s.WeightIo, MinWeight, MaxWeight),
        WeightNet = Math.Clamp(s.WeightNet, MinWeight, MaxWeight),
        WeightMemory = Math.Clamp(s.WeightMemory, MinWeight, MaxWeight),
        WeightGpu = Math.Clamp(s.WeightGpu, MinWeight, MaxWeight),
        GpuSpikePercent = Math.Clamp(s.GpuSpikePercent, MinGpu, MaxGpu),
        DiskLatencySpikeMs = Math.Clamp(s.DiskLatencySpikeMs, MinDiskLatency, MaxDiskLatency),
        CommitSpikePercent = Math.Clamp(s.CommitSpikePercent, MinCommit, MaxCommit),
        IdleAfterSeconds = Math.Clamp(s.IdleAfterSeconds, MinIdle, MaxIdle),
        MinAnomalySeconds = Math.Clamp(s.MinAnomalySeconds, MinMinAnomaly, MaxMinAnomaly),
        MarkerHotkey = HotkeyGesture.TryParse(s.MarkerHotkey, out _) ? s.MarkerHotkey : AppSettings.Default.MarkerHotkey,
    };

    private static void Range(List<string> e, string name, double v, double min, double max, string unit)
    {
        if (v < min || v > max || double.IsNaN(v))
            e.Add($"{name} must be between {min} and {max} {unit}".TrimEnd() + ".");
    }
}
