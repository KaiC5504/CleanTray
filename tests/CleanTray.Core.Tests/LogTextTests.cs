namespace CleanTray.Core.Tests;

public sealed class LogTextTests
{
    [Fact]
    public void Icon_HasIdPidCapturedAndExeNameOnly()
    {
        var e = new IconUpsertEvent(7, 1234, 0x10, 1, false, Guid.Empty, 0x8001, 4, 0, true,
            @"c:\users\someone\secret\app.exe", @"C:\Users\Someone\Secret\App.exe", "Bank balance: 1,000",
            0, 0, []);

        var text = LogText.Icon(e);

        Assert.Equal("icon 7: pid 1234 captured=True exe=App.exe", text);
    }

    [Fact]
    public void Error_HasTypeAndHResultButNeverTheMessage()
    {
        var e = new FileNotFoundException(@"Could not find C:\Users\Someone\Secret\x.json");

        var text = LogText.Error(e);

        Assert.StartsWith("FileNotFoundException 0x", text);
        Assert.DoesNotContain("Secret", text);
    }
}
