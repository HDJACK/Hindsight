using Hindsight.Core.Settings;

namespace Hindsight.Core.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+M", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x4D)]
    [InlineData("ctrl + shift + f9", HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x78)]
    [InlineData("Win+1", HotkeyGesture.ModWin, 0x31)]
    [InlineData("Alt+Space", HotkeyGesture.ModAlt, 0x20)]
    [InlineData("P", 0, 0x50)]
    [InlineData("F9", 0, 0x78)]
    public void TryParse_Accepts(string text, uint mods, uint vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g!.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
    }

    [Theory]
    [InlineData("")] [InlineData("Ctrl+")] [InlineData("Ctrl+Banana")] [InlineData("Ctrl+Alt")]
    public void TryParse_Rejects(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void ToString_UnknownKey_DoesNotThrow()
    {
        Assert.Equal("Ctrl+VK0xC0", new HotkeyGesture(HotkeyGesture.ModControl, 0xC0).ToString());
    }

    [Fact]
    public void ToString_RoundTrips_Canonical()
    {
        Assert.True(HotkeyGesture.TryParse("shift+ctrl+m", out var g));
        Assert.Equal("Ctrl+Shift+M", g!.ToString());
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var g2));
        Assert.Equal(g, g2);
    }

    [Fact]
    public void ToString_RoundTrips_BareKey()
    {
        Assert.True(HotkeyGesture.TryParse("f9", out var g));
        Assert.Equal("F9", g!.ToString());
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var g2));
        Assert.Equal(g, g2);
    }
}
