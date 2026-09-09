using System.Runtime.InteropServices;
using System.Windows.Interop;
using Hindsight.Core.Settings;

namespace Hindsight.App.Services;

/// <summary>Global hotkey on a message-only window. Rebind() swaps the gesture at runtime.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 1;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Action _onHotkey;
    private bool _disposed;

    public bool Registered { get; private set; }
    public HotkeyGesture Gesture { get; private set; }

    public HotkeyService(HotkeyGesture gesture, Action onHotkey)
    {
        _onHotkey = onHotkey;
        Gesture = gesture;
        var p = new HwndSourceParameters("HindsightHotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_source.Handle, HotkeyId, gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey);
    }

    public bool Rebind(HotkeyGesture gesture)
    {
        if (Registered) UnregisterHotKey(_source.Handle, HotkeyId);
        Gesture = gesture;
        Registered = RegisterHotKey(_source.Handle, HotkeyId, gesture.Modifiers | MOD_NOREPEAT, gesture.VirtualKey);
        return Registered;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId) { _onHotkey(); handled = true; }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Registered) UnregisterHotKey(_source.Handle, HotkeyId);
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
