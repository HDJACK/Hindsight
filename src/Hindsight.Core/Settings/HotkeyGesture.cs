namespace Hindsight.Core.Settings;

/// <summary>A global hotkey as RegisterHotKey wants it: modifier flags plus a virtual-key code.</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey)
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    private static readonly Dictionary<string, uint> Keys = BuildKeys();

    private static Dictionary<string, uint> BuildKeys()
    {
        var d = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = (uint)c;
        for (char c = '0'; c <= '9'; c++) d[c.ToString()] = (uint)c;
        for (int i = 1; i <= 12; i++) d["F" + i] = (uint)(0x6F + i);
        d["Space"] = 0x20; d["Pause"] = 0x13; d["Insert"] = 0x2D; d["Delete"] = 0x2E;
        d["Home"] = 0x24; d["End"] = 0x23; d["PageUp"] = 0x21; d["PageDown"] = 0x22;
        return d;
    }

    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        uint mods = 0; uint vk = 0; bool haveKey = false;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            if (part.Length == 0) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModControl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": case "windows": mods |= ModWin; break;
                default:
                    if (haveKey || !Keys.TryGetValue(part, out vk)) return false;
                    haveKey = true;
                    break;
            }
        }
        if (!haveKey) return false;
        gesture = new HotkeyGesture(mods, vk);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(Keys.FirstOrDefault(k => k.Value == VirtualKey).Key ?? ("VK0x" + VirtualKey.ToString("X2")));
        return string.Join("+", parts);
    }
}
