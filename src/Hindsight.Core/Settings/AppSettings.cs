using Hindsight.Core.Model;

namespace Hindsight.Core.Settings;

/// <summary>Which colour theme the app follows.</summary>
public enum AppTheme { System, Light, Dark }

/// <summary>The main window's last position and size, restored on the next start.</summary>
public sealed record WindowBounds(double Left, double Top, double Width, double Height);

/// <summary>All user-configurable settings. Init-only so the store can hand out immutable snapshots.</summary>
public sealed record AppSettings
{
    public static string DefaultDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hindsight");

    public static AppSettings Default => new();

    // Recording
    public int BufferMinutes { get; init; } = 30;
    public int SampleIntervalSeconds { get; init; } = 1;
    public string DataFolder { get; init; } = DefaultDataFolder;
    public bool CaptureFilePaths { get; init; } = true;
    public bool CaptureDnsAndEndpoints { get; init; } = true;

    // Detection
    public double CpuSpikePercent { get; init; } = 80;
    public double FileIoSpikeMBps { get; init; } = 300;
    public double NetworkSpikeMBps { get; init; } = 50;
    public int BaselineWindowSeconds { get; init; } = 300;
    public double WeightCpu { get; init; } = 1;
    public double WeightIo { get; init; } = 1;
    public double WeightNet { get; init; } = 1;
    public double WeightMemory { get; init; } = 0;
    public double GpuSpikePercent { get; init; } = 90;
    public double DiskLatencySpikeMs { get; init; } = 50;
    public double CommitSpikePercent { get; init; } = 90;
    public double WeightGpu { get; init; } = 1;
    public int IdleAfterSeconds { get; init; } = 60;
    public AnomalySensitivity AnomalySensitivity { get; init; } = AnomalySensitivity.Normal;
    public int MinAnomalySeconds { get; init; } = 2;

    [System.Text.Json.Serialization.JsonIgnore]
    public double SensitivityK => AnomalySensitivity switch { AnomalySensitivity.Low => 6, AnomalySensitivity.High => 3, _ => 4 };

    // General
    public string MarkerHotkey { get; init; } = "Ctrl+Alt+M";
    public bool StartWithWindows { get; init; } = false;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public bool StoreCommandLines { get; init; } = true;
    public WindowBounds? WindowBounds { get; init; }
    public bool ShowForegroundApp { get; init; } = true;
    public DetailLevel DetailLevel { get; init; } = DetailLevel.Easy;
    public string VisibleLanes { get; init; } = DefaultVisibleLanes;

    [System.Text.Json.Serialization.JsonIgnore]
    public int RingCapacity => Math.Max(1, BufferMinutes * 60 / SampleIntervalSeconds);

    private const string DefaultVisibleLanes = "Cpu,Gpu,FileIo,Network,RamFree";

    public IReadOnlyList<LaneKind> VisibleLaneKinds()
    {
        var lanes = ParseLanes(VisibleLanes);
        return lanes.Count > 0 ? lanes : ParseLanes(DefaultVisibleLanes);
    }

    private static List<LaneKind> ParseLanes(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Enum.TryParse<LaneKind>(name, ignoreCase: true, out var kind) ? (LaneKind?)kind : null)
            .Where(kind => kind.HasValue)
            .Select(kind => kind!.Value)
            .Distinct()
            .ToList();

    public static string LanesToString(IEnumerable<LaneKind> lanes) => string.Join(",", lanes);
}
