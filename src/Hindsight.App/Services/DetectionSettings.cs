using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

/// <summary>Compares the settings the anomaly detector reads, so a page only re-runs detection when one of them changed.</summary>
public static class DetectionSettings
{
    /// <summary>True when any field <see cref="Hindsight.Core.Analysis.AnomalyDetector"/> reads differs between the two settings.</summary>
    public static bool Changed(AppSettings a, AppSettings b) =>
        a.BaselineWindowSeconds != b.BaselineWindowSeconds
        || a.AnomalySensitivity != b.AnomalySensitivity
        || a.MinAnomalySeconds != b.MinAnomalySeconds
        || a.CpuSpikePercent != b.CpuSpikePercent
        || a.FileIoSpikeMBps != b.FileIoSpikeMBps
        || a.NetworkSpikeMBps != b.NetworkSpikeMBps
        || a.GpuSpikePercent != b.GpuSpikePercent
        || a.DiskLatencySpikeMs != b.DiskLatencySpikeMs
        || a.CommitSpikePercent != b.CommitSpikePercent;
}
