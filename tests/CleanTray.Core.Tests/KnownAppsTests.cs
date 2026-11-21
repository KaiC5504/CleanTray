namespace CleanTray.Core.Tests;

public sealed class KnownAppsTests
{
    static readonly Guid ProgramFiles = new("6D809377-6AF0-444B-8957-A3773F02200E");
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    static string? Folders(Guid id) => id == ProgramFiles ? @"C:\Program Files" : null;

    static KnownAppSystem System(Func<string, bool>? exists = null, Func<string, string?>? description = null) =>
        new(Folders, exists ?? (_ => true), path => path.ToLowerInvariant(), description ?? (_ => null));

    static Dictionary<string, object?> Entry(object? exe, object? tip = null, object? snapshot = null, object? uid = null)
    {
        var e = new Dictionary<string, object?> { ["ExecutablePath"] = exe };
        if (tip is not null) e["InitialTooltip"] = tip;
        if (snapshot is not null) e["IconSnapshot"] = snapshot;
        if (uid is not null) e["UID"] = uid;
        return e;
    }

    [Fact]
    public void Resolve_ExpandsAKnownFolderPrefix()
    {
        Assert.Equal(@"C:\Program Files\Contoso\Contoso App\ContosoApp.exe",
            KnownFolderPath.Resolve(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Contoso\Contoso App\ContosoApp.exe", Folders));
    }

    [Fact]
    public void Resolve_IgnoresTheGuidsCase()
    {
        Assert.Equal(@"C:\Program Files\a.exe", KnownFolderPath.Resolve(@"{6d809377-6af0-444b-8957-a3773f02200e}\a.exe", Folders));
    }

    [Fact]
    public void Resolve_FolderWithATrailingSlashDoesNotDoubleIt()
    {
        Assert.Equal(@"C:\a.exe", KnownFolderPath.Resolve(@"{6D809377-6AF0-444B-8957-A3773F02200E}\a.exe", _ => @"C:\"));
    }

    [Fact]
    public void Resolve_LeavesAPlainPathAlone()
    {
        Assert.Equal(@"D:\Apps\Example\example.exe", KnownFolderPath.Resolve(@"D:\Apps\Example\example.exe", Folders));
    }

    [Theory]
    [InlineData(@"{11111111-2222-3333-4444-555555555555}\a.exe")]
    [InlineData(@"{not-a-guid}\a.exe")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}")]
    [InlineData("")]
    public void Resolve_UnknownOrMalformedIsNull(string path)
    {
        Assert.Null(KnownFolderPath.Resolve(path, Folders));
    }

    [Fact]
    public void From_MapsAnEntry()
    {
        var app = Assert.Single(KnownApps.From(
            [Entry(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Contoso\ContosoApp.exe", "Contoso App", Png, 1)],
            System(description: _ => "Contoso App Desktop")));

        Assert.Equal(@"c:\program files\contoso\contosoapp.exe", app.Key);
        Assert.Equal(@"C:\Program Files\Contoso\ContosoApp.exe", app.ExePath);
        Assert.Equal("Contoso App Desktop", app.DisplayName);
        Assert.Equal(Png, app.IconPng);
    }

    [Fact]
    public void From_NameFallsBackToTheTooltipsFirstLineThenTheFileName()
    {
        var apps = KnownApps.From(
            [Entry(@"C:\A\one.exe", " Tool One \nsecond line"), Entry(@"C:\A\two.exe", "  ")],
            System());

        Assert.Equal(["Tool One", "two"], apps.Select(a => a.DisplayName));
    }

    [Fact]
    public void From_SkipsMissingFilesUnknownFoldersAndBadPaths()
    {
        var apps = KnownApps.From(
            [
                Entry(@"C:\A\gone.exe"),
                Entry(@"{11111111-2222-3333-4444-555555555555}\a.exe"),
                Entry(@"relative\a.exe"),
                Entry(""),
                Entry(null),
                Entry(42),
                new Dictionary<string, object?>(),
                Entry(@"C:\A\here.exe"),
            ],
            System(exists: p => !p.EndsWith("gone.exe", StringComparison.Ordinal)));

        Assert.Equal([@"C:\A\here.exe"], apps.Select(a => a.ExePath));
    }

    [Fact]
    public void From_SkipsEntriesTheDllCannotKey()
    {
        var system = System() with { AppKey = _ => null };
        Assert.Empty(KnownApps.From([Entry(@"C:\A\a.exe")], system));
    }

    [Fact]
    public void From_IgnoresWrongTypesAndNonPngSnapshots()
    {
        var apps = KnownApps.From(
            [Entry(@"C:\A\a.exe", tip: 7, snapshot: "png"), Entry(@"C:\A\b.exe", snapshot: new byte[] { 1, 2, 3 })],
            System());

        Assert.All(apps, a => Assert.Null(a.IconPng));
        Assert.Equal("a", apps[0].DisplayName);
    }

    [Fact]
    public void From_OneAppPerKeyFillingGapsFromLaterEntries()
    {
        var descriptions = 0;
        var apps = KnownApps.From(
            [Entry(@"C:\A\App.exe"), Entry(@"c:\a\app.exe", "Later tip", Png)],
            System(description: _ => { descriptions++; return null; }));

        var app = Assert.Single(apps);
        Assert.Equal(@"C:\A\App.exe", app.ExePath);
        Assert.Equal("Later tip", app.DisplayName);
        Assert.Equal(Png, app.IconPng);
        Assert.Equal(1, descriptions);
    }
}
