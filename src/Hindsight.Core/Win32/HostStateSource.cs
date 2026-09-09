using Hindsight.Core.Model;
using Hindsight.Core.Settings;
using Hindsight.Core.Sources;
using static Hindsight.Core.Win32.NativeMethods;

namespace Hindsight.Core.Win32;

/// <summary>
/// Host-wide state that isn't per-process: the foreground window's owning process, whether the
/// user is idle (no input for longer than <see cref="AppSettings.IdleAfterSeconds"/>), and power
/// status (AC/battery, battery percent). Reports no process deltas or start/exit events.
/// </summary>
public sealed class HostStateSource : ISampleSource
{
    public string Name => "host";

    private readonly Func<AppSettings> _settings;
    private readonly Func<int> _foregroundPid;
    private readonly Func<uint> _lastInputTick;
    private readonly Func<uint> _nowTick;
    private readonly Func<(bool? onAc, sbyte battery)> _power;

    public HostStateSource(Func<AppSettings> settings, Func<int>? foregroundPid = null, Func<uint>? lastInputTick = null,
        Func<uint>? nowTick = null, Func<(bool? onAc, sbyte battery)>? power = null)
    {
        _settings = settings;
        _foregroundPid = foregroundPid ?? ReadForegroundPid;
        _lastInputTick = lastInputTick ?? ReadLastInputTick;
        _nowTick = nowTick ?? GetTickCount;
        _power = power ?? ReadPower;
    }

    public void Start() { }

    public SourceSnapshot Read()
    {
        int foregroundPid = _foregroundPid();
        uint now = _nowTick();
        uint lastInput = _lastInputTick();
        uint idleMs = unchecked(now - lastInput);
        bool idle = idleMs / 1000 > _settings().IdleAfterSeconds;
        var (onAc, battery) = _power();

        return new SourceSnapshot
        {
            Totals = new SystemTotals(0, 0, 0, ForegroundPid: foregroundPid, UserIdle: idle, OnAc: onAc, BatteryPercent: battery)
        };
    }

    private static int ReadForegroundPid()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hWnd, out uint pid);
        return (int)pid;
    }

    private static uint ReadLastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : GetTickCount();
    }

    private static (bool? onAc, sbyte battery) ReadPower()
    {
        bool? onAc = PowerInfo.IsOnAcPower();
        sbyte battery = -1;
        // BatteryFlag 128 = no system battery (desktops report percent 0 then); 255 = unknown.
        if (GetSystemPowerStatus(out var status) && status.BatteryLifePercent != 255 && (status.BatteryFlag & 0x80) == 0)
            battery = (sbyte)Math.Clamp(status.BatteryLifePercent, (byte)0, (byte)100);
        return (onAc, battery);
    }

    public void Dispose() { }
}
