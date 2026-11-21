namespace CleanTray.Core.Tests;

public sealed class ModeChoiceTests
{
    [Theory]
    [InlineData(0, RuleMode.Show)]
    [InlineData(1, RuleMode.Captured)]
    public void TryApp_MapsTheTwoChoices(int index, RuleMode expected)
    {
        Assert.True(ModeChoice.TryApp(index, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MinValue)]
    public void TryApp_RejectsAnythingElse(int index)
    {
        Assert.False(ModeChoice.TryApp(index, out _));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, RuleMode.Show)]
    [InlineData(2, RuleMode.Captured)]
    public void TryIcon_ZeroMeansSameAsApp(int index, RuleMode? expected)
    {
        Assert.True(ModeChoice.TryIcon(index, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void TryIcon_RejectsAnythingElse(int index)
    {
        Assert.False(ModeChoice.TryIcon(index, out _));
    }

    [Fact]
    public void Indexes_RoundTrip()
    {
        foreach (var m in new[] { RuleMode.Show, RuleMode.Captured })
        {
            Assert.True(ModeChoice.TryApp(ModeChoice.AppIndex(m), out var back));
            Assert.Equal(m, back);
        }
        foreach (RuleMode? m in new RuleMode?[] { null, RuleMode.Show, RuleMode.Captured })
        {
            Assert.True(ModeChoice.TryIcon(ModeChoice.IconIndex(m), out var back));
            Assert.Equal(m, back);
        }
    }
}
