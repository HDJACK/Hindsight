using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>Reads the machine's power source.</summary>
public static class PowerInfo
{
    /// <summary>true = AC, false = battery, null = unknown.</summary>
    public static bool? IsOnAcPower()
    {
        if (!GetSystemPowerStatus(out var s)) return null;
        return s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
    }
}
