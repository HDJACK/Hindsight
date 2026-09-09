using System.Diagnostics;

namespace Hindsight.Core.Win32;

/// <summary>Start-with-Windows via a Task Scheduler task with highest run level, so no UAC prompt appears at logon.</summary>
public static class AutostartTask
{
    public const string TaskName = "Hindsight";

    public static string CreateArguments(string exePath) =>
        $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{exePath}\\\"\"";

    public static string DeleteArguments() => $"/Delete /TN \"{TaskName}\" /F";

    public static string QueryArguments() => $"/Query /TN \"{TaskName}\"";

    public static (bool ok, string output) Enable(string exePath) => Run(CreateArguments(exePath));

    public static (bool ok, string output) Disable() => Run(DeleteArguments());

    public static bool IsEnabled() => Run(QueryArguments()).ok;

    private static (bool ok, string output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode == 0, output.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
