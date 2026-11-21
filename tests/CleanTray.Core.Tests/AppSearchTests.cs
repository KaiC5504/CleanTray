namespace CleanTray.Core.Tests;

public sealed class AppSearchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Matches_BlankQueryMatchesEverything(string? query)
    {
        Assert.False(AppSearch.HasQuery(query));
        Assert.True(AppSearch.Matches("Contoso Sync", @"C:\Apps\sync.exe", query));
    }

    [Theory]
    [InlineData("contoso")]
    [InlineData("CONTOSO")]
    [InlineData("so syn")]
    [InlineData("  contoso  ")]
    public void Matches_DisplayNameIgnoringCase(string query)
    {
        Assert.True(AppSearch.HasQuery(query));
        Assert.True(AppSearch.Matches("Contoso Sync", @"C:\Apps\x.exe", query));
    }

    [Theory]
    [InlineData("sync.exe")]
    [InlineData("SYNC.EXE")]
    [InlineData("ync")]
    public void Matches_ExeFileName(string query) =>
        Assert.True(AppSearch.Matches("Something Else", @"C:\Apps\Contoso\Sync.exe", query));

    [Fact]
    public void Matches_DoesNotLookInTheFolders()
    {
        Assert.False(AppSearch.Matches("Something Else", @"C:\Apps\Contoso\sync.exe", "contoso"));
        Assert.False(AppSearch.Matches("Something Else", @"C:\Apps\Contoso\sync.exe", "apps"));
    }

    [Fact]
    public void Matches_ExePathWithAKnownFolderPrefix()
    {
        Assert.True(AppSearch.Matches("N", @"{6D809377-6AF0-444B-8957-A3773F02200E}\Fabrikam\agent.exe", "agent"));
    }

    [Fact]
    public void Matches_NoMatch() =>
        Assert.False(AppSearch.Matches("Contoso Sync", @"C:\Apps\sync.exe", "zzz"));

    [Fact]
    public void Matches_NonAsciiNames() =>
        Assert.True(AppSearch.Matches("示例同步", @"C:\Apps\sync.exe", "例同"));

    [Theory]
    [InlineData(false, null, 5, false)]
    [InlineData(false, "", 5, false)]
    [InlineData(false, "   ", 5, false)]
    [InlineData(false, "abc", 0, false)]
    [InlineData(false, "abc", 2, true)]
    [InlineData(true, null, 0, true)]
    [InlineData(true, "abc", 0, true)]
    public void OtherExpanded_OnlyWhenOpenedOrSearchFindsSomething(bool user, string? query, int matches, bool expected) =>
        Assert.Equal(expected, AppSearch.OtherExpanded(user, query, matches));

    [Fact]
    public void OtherTitle_CarriesTheCount()
    {
        Assert.Equal("Other apps Windows knows about (0)", AppSearch.OtherTitle(0));
        Assert.Equal("Other apps Windows knows about (63)", AppSearch.OtherTitle(63));
    }
}
