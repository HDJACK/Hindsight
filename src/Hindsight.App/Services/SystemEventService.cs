using System.Runtime.InteropServices;
using System.Windows.Interop;
using Hindsight.Core.Model;
using Hindsight.Core.Win32;
using Microsoft.Win32;

namespace Hindsight.App.Services;

/// <summary>Markers for suspend/resume, lock/unlock, AC power changes, USB arrival/removal and display on/off.</summary>
public sealed class SystemEventService : IDisposable
{
    private const int WM_POWERBROADCAST = 0x0218;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const uint DBT_DEVTYP_DEVICEINTERFACE = 5;
    private const uint DBT_DEVTYP_VOLUME = 2;
    private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GuidDevInterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    /// <summary>Ignore a second marker of the same kind within this window (device changes arrive in bursts).</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public uint dbch_size;
        public uint dbch_devicetype;
        public uint dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public uint size;
        public uint devicetype;
        public uint reserved;
        public Guid classguid;
        public char name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hwnd, ref Guid guid, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hwnd, IntPtr filter, int flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private readonly AppServices _services;
    private readonly HwndSource _source;
    private readonly Dictionary<MarkerKind, DateTime> _lastMarker = new();
    private IntPtr _powerNotify;
    private IntPtr _deviceNotify;
    private bool? _lastAc;
    private bool _disposed;

    public SystemEventService(AppServices services)
    {
        _services = services;
        _lastAc = PowerInfo.IsOnAcPower();
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionSwitch += OnSession;

        var p = new HwndSourceParameters("HindsightSystemEvents") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(Hook);

        var displayGuid = GuidConsoleDisplayState;
        _powerNotify = RegisterPowerSettingNotification(_source.Handle, ref displayGuid, 0);
        if (_powerNotify == IntPtr.Zero)
            _services.AddContextNote("Display markers unavailable (power notification registration failed)");

        _deviceNotify = RegisterUsbNotification(_source.Handle);
        if (_deviceNotify == IntPtr.Zero)
            _services.AddContextNote("USB markers unavailable (device notification registration failed)");
    }

    private static IntPtr RegisterUsbNotification(IntPtr hwnd)
    {
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            size = (uint)Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            classguid = GuidDevInterfaceUsbDevice,
        };
        IntPtr buffer = Marshal.AllocHGlobal((int)filter.size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            return RegisterDeviceNotification(hwnd, buffer, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && wParam.ToInt32() == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GuidConsoleDisplayState)
            {
                if (setting.Data == 0) AddDebounced(MarkerKind.DisplayOff, "Display off");
                else if (setting.Data == 1) AddDebounced(MarkerKind.DisplayOn, "Display on");
                handled = true;
            }
        }
        else if (msg == WM_DEVICECHANGE && lParam != IntPtr.Zero)
        {
            int action = wParam.ToInt32();
            if (action == DBT_DEVICEARRIVAL || action == DBT_DEVICEREMOVECOMPLETE)
            {
                var header = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
                if (header.dbch_devicetype is DBT_DEVTYP_VOLUME or DBT_DEVTYP_DEVICEINTERFACE)
                {
                    if (action == DBT_DEVICEARRIVAL) AddDebounced(MarkerKind.UsbArrived, "USB device connected");
                    else AddDebounced(MarkerKind.UsbRemoved, "USB device removed");
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    private void AddDebounced(MarkerKind kind, string text)
    {
        var now = DateTime.UtcNow;
        if (_lastMarker.TryGetValue(kind, out var last) && now - last < DebounceWindow) return;
        _lastMarker[kind] = now;
        _services.AddMarker(kind, text);
    }

    private void OnPower(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend: _services.AddMarker(MarkerKind.Suspend, "Suspend"); break;
            case PowerModes.Resume: _services.AddMarker(MarkerKind.Resume, "Resume"); break;
            case PowerModes.StatusChange:
                var now = PowerInfo.IsOnAcPower();
                if (now is null || now == _lastAc) break;
                _lastAc = now;
                _services.AddMarker(now.Value ? MarkerKind.AcConnected : MarkerKind.AcDisconnected,
                    now.Value ? "AC power connected" : "AC power disconnected");
                break;
        }
    }

    private void OnSession(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) _services.AddMarker(MarkerKind.Lock, "Locked");
        else if (e.Reason == SessionSwitchReason.SessionUnlock) _services.AddMarker(MarkerKind.Unlock, "Unlocked");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.PowerModeChanged -= OnPower;
        SystemEvents.SessionSwitch -= OnSession;
        if (_powerNotify != IntPtr.Zero) { UnregisterPowerSettingNotification(_powerNotify); _powerNotify = IntPtr.Zero; }
        if (_deviceNotify != IntPtr.Zero) { UnregisterDeviceNotification(_deviceNotify); _deviceNotify = IntPtr.Zero; }
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
