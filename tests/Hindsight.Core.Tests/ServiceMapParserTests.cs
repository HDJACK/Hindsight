using Hindsight.Core.Context;

namespace Hindsight.Core.Tests;

public class ServiceMapParserTests
{
    [Fact]
    public void Build_GroupsSortsAndSkips()
    {
        var map = ServiceMapParser.Build(new[] { ("wuauserv", 900), ("BITS", 900), ("bits", 900), ("", 900), ("Spooler", 1200), ("Stopped", 0) });
        Assert.Equal(2, map.Count);
        Assert.Equal("BITS, wuauserv", map[900]);
        Assert.Equal("Spooler", map[1200]);
    }
}
