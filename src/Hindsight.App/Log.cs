using System.IO;
using Hindsight.Core.Settings;

namespace Hindsight.App;

/// <summary>Minimal append-only diagnostics log in the data folder (hindsight.log). Never throws.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly string Path = System.IO.Path.Combine(AppSettings.DefaultDataFolder, "hindsight.log");

    public static void Info(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics only */ }
    }

    public static void Error(string context, Exception ex) => Info($"ERROR {context}: {ex}");
}
