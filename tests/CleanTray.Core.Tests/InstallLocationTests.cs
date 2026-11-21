namespace CleanTray.Core.Tests;

public sealed class InstallLocationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "CleanTray-install-" + Guid.NewGuid().ToString("N"));

    public InstallLocationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Exe => Path.Combine(_dir, "CleanTray.exe");

    [Fact]
    public void IsInstalled_TrueWithTheMarkerNextToTheExe()
    {
        File.WriteAllText(Path.Combine(_dir, InstallLocation.MarkerFileName), "");
        Assert.True(InstallLocation.IsInstalled(Exe));
    }

    [Fact]
    public void IsInstalled_FalseInADevBuildFolderWithoutTheMarker()
    {
        Assert.False(InstallLocation.IsInstalled(Exe));
    }

    [Fact]
    public void IsInstalled_FalseWhenTheMarkerIsOnlyInAParentFolder()
    {
        File.WriteAllText(Path.Combine(_dir, InstallLocation.MarkerFileName), "");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        Assert.False(InstallLocation.IsInstalled(Path.Combine(sub, "CleanTray.exe")));
    }

    [Fact]
    public void IsInstalled_FalseWhenTheMarkerIsADirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, InstallLocation.MarkerFileName));
        Assert.False(InstallLocation.IsInstalled(Exe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CleanTray.exe\0")]
    public void IsInstalled_FalseForUnusablePaths(string exe)
    {
        Assert.False(InstallLocation.IsInstalled(exe));
    }
}
