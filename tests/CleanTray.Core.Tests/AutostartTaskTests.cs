using System.Xml.Linq;

namespace CleanTray.Core.Tests;

public sealed class AutostartTaskTests
{
    static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    static XElement Root(string user = @"PC\User", string exe = @"C:\Users\User\AppData\Local\Programs\CleanTray\CleanTray.exe") =>
        XDocument.Parse(AutostartTask.Xml(user, exe)).Root!;

    [Fact]
    public void Xml_LogonTriggerForTheUserWithNoDelay()
    {
        var trigger = Root().Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!;

        Assert.Equal(@"PC\User", trigger.Element(Ns + "UserId")!.Value);
        Assert.Equal("true", trigger.Element(Ns + "Enabled")!.Value);
        Assert.Null(trigger.Element(Ns + "Delay"));
    }

    [Fact]
    public void Xml_RunsWithTheInteractiveTokenAtLeastPrivilege()
    {
        var principal = Root().Element(Ns + "Principals")!.Element(Ns + "Principal")!;

        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        Assert.Equal("LeastPrivilege", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal(@"PC\User", principal.Element(Ns + "UserId")!.Value);
    }

    [Fact]
    public void Xml_NeverStopsOrBlocksOnBattery()
    {
        var settings = Root().Element(Ns + "Settings")!;

        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
    }

    [Fact]
    public void Xml_NormalPriorityAndWorkingDirectoryIsTheExeFolder()
    {
        var root = Root(exe: @"C:\Users\User\AppData\Local\Programs\CleanTray\CleanTray.exe");

        Assert.Equal("5", root.Element(Ns + "Settings")!.Element(Ns + "Priority")!.Value);
        Assert.Equal(@"C:\Users\User\AppData\Local\Programs\CleanTray",
            root.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void CommandPath_ReadsBackWhatXmlWrote()
    {
        var exe = @"C:\Program Files\CleanTray & Co\CleanTray.exe";

        Assert.Equal(exe, AutostartTask.CommandPath(AutostartTask.Xml(@"PC\User", exe)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\" />")]
    public void CommandPath_NullWhenUnreadable(string xml)
    {
        Assert.Null(AutostartTask.CommandPath(xml));
    }

    [Fact]
    public void Xml_StartsTheQuotedExe()
    {
        var command = Root(exe: @"C:\Program Files\CleanTray & Co\CleanTray.exe")
            .Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value;

        Assert.Equal("\"C:\\Program Files\\CleanTray & Co\\CleanTray.exe\"", command);
    }
}
