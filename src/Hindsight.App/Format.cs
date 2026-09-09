using System.Globalization;

namespace Hindsight.App;

public static class Format
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    public static string Bytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return (b / 1024.0).ToString("0.#", En) + " KB";
        if (b < 1024L * 1024 * 1024) return (b / 1048576.0).ToString("0.#", En) + " MB";
        return (b / 1073741824.0).ToString("0.##", En) + " GB";
    }

    public static string Rate(long bytesPerTick, int intervalSeconds) =>
        Bytes((long)(bytesPerTick / (double)Math.Max(1, intervalSeconds))) + "/s";

    public static string Percent(double v) => v.ToString("0.0", En) + " %";
    public static string Time(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm:ss", En);
    public static string TimeShort(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm", En);

    public static string Duration(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0 ? $"{h}h {m:00}m {s:00}s" : m > 0 ? $"{m:00}m {s:00}s" : $"{s:00}s";
    }
}
