namespace CleanTray.Core.Tests;

public sealed class DisplayNamesTests
{
    [Fact]
    public void Choose_PrefersTheFileDescription()
    {
        Assert.Equal("Discord", DisplayNames.Choose("  Discord ", @"C:\x\Discord.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Choose_FallsBackToTheFileNameWithoutExtension(string? description)
    {
        Assert.Equal("Discord", DisplayNames.Choose(description, @"C:\x\Discord.exe"));
    }

    [Fact]
    public void For_MissingFileFallsBackToTheFileName()
    {
        Assert.Equal("gone", DisplayNames.For(@"C:\definitely\not\here\gone.exe"));
    }

    [Fact]
    public void For_BadPathDoesNotThrow()
    {
        Assert.Equal("", DisplayNames.For(""));
    }
}
