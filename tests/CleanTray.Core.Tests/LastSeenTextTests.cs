using System.Globalization;

namespace CleanTray.Core.Tests;

public sealed class LastSeenTextTests
{
    static readonly DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Format_NotSeenYetForADefaultDate() => Assert.Equal("Not seen yet", LastSeenText.Format(default, Now));

    [Theory]
    [InlineData(0, "Just now")]
    [InlineData(59, "Just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(23 * 3600, "23 h ago")]
    public void Format_RelativeWithinADay(int secondsAgo, string expected)
    {
        Assert.Equal(expected, LastSeenText.Format(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void Format_OlderShowsTheLocalDate()
    {
        var seen = new DateTime(2026, 10, 3, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(seen.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            LastSeenText.Format(seen, Now));
    }

    [Fact]
    public void Format_ClockSkewIntoTheFutureIsJustNow()
    {
        Assert.Equal("Just now", LastSeenText.Format(Now.AddMinutes(5), Now));
    }
}
