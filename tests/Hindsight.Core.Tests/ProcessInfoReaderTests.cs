using Hindsight.Core.Win32;

namespace Hindsight.Core.Tests;

public class ProcessInfoReaderTests
{
    [Fact]
    public void ReadVersionInfo_Notepad_HasCompany()
    {
        var (desc, company) = ProcessInfoReader.ReadVersionInfo(@"C:\Windows\System32\notepad.exe");
        Assert.Contains("Microsoft", company);
        Assert.NotEqual("", desc);
        Assert.Equal(("", ""), ProcessInfoReader.ReadVersionInfo(@"C:\does\not\exist.exe"));
    }

    [Fact]
    public void ServiceMapSource_Read_ReturnsMapOnThisMachine()
    {
        using var src = new ServiceMapSource();
        src.Start();
        var snap = src.Read();
        Assert.NotNull(snap.Services);
        Assert.True(snap.Services!.Count > 5, src.LastError);
        Assert.Null(src.Read().Services);   // not due yet
    }
}
