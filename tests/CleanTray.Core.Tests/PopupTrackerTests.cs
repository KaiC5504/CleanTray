namespace CleanTray.Core.Tests;

public sealed class PopupTrackerTests
{
    static readonly TimeSpan Long = TimeSpan.FromMinutes(5);

    static (PopupTracker Tracker, List<PopupResolution> Seen) Make(TimeSpan expiry)
    {
        var seen = new List<PopupResolution>();
        var tracker = new PopupTracker(r =>
        {
            lock (seen) seen.Add(r);
        }, expiry);
        return (tracker, seen);
    }

    [Fact]
    public void Resolve_ReportsOnceWithTheIconAndPid()
    {
        var (t, seen) = Make(Long);
        using var _ = t;
        var token = t.Add(7, 1234);

        Assert.True(t.Resolve(token, PopupOutcome.Clicked));
        Assert.False(t.Resolve(token, PopupOutcome.TimedOut));

        var r = Assert.Single(seen);
        Assert.Equal(new PopupResolution(token, 7, 1234, PopupOutcome.Clicked), r);
    }

    [Fact]
    public void Resolve_UnknownTokenDoesNothing()
    {
        var (t, seen) = Make(Long);
        using var _ = t;

        Assert.False(t.Resolve("nope", PopupOutcome.Clicked));
        Assert.Empty(seen);
    }

    [Fact]
    public void Tokens_AreUniquePerPopupOfTheSameIcon()
    {
        var (t, seen) = Make(Long);
        using var _ = t;
        var a = t.Add(7, 1);
        var b = t.Add(7, 1);

        Assert.NotEqual(a, b);
        t.Resolve(b, PopupOutcome.Clicked);
        t.Resolve(a, PopupOutcome.TimedOut);
        Assert.Equal([PopupOutcome.Clicked, PopupOutcome.TimedOut], seen.Select(r => r.Outcome));
    }

    [Fact]
    public void Expiry_ResolvesTimedOutWhenNothingElseDid()
    {
        var (t, seen) = Make(TimeSpan.FromMilliseconds(50));
        using var _ = t;
        var token = t.Add(9, 5);

        Assert.True(SpinWait.SpinUntil(() => { lock (seen) return seen.Count == 1; }, TimeSpan.FromSeconds(5)));
        Assert.Equal(PopupOutcome.TimedOut, seen[0].Outcome);
        Assert.False(t.Resolve(token, PopupOutcome.Clicked));
        Thread.Sleep(150);
        Assert.Single(seen);
    }

    [Fact]
    public void Expiry_DoesNotFireAfterAResolve()
    {
        var (t, seen) = Make(TimeSpan.FromMilliseconds(80));
        using var _ = t;
        t.Resolve(t.Add(1, 1), PopupOutcome.Clicked);

        Thread.Sleep(300);

        Assert.Equal([PopupOutcome.Clicked], seen.Select(r => r.Outcome));
    }

    [Fact]
    public void ResolveAll_ReportsEveryPendingOnceAndCancelsTheirTimers()
    {
        var (t, seen) = Make(TimeSpan.FromMilliseconds(100));
        using var _ = t;
        t.Add(1, 1);
        t.Add(2, 2);
        var done = t.Add(3, 3);
        t.Resolve(done, PopupOutcome.Clicked);
        seen.Clear();

        t.ResolveAll(PopupOutcome.TimedOut);
        Thread.Sleep(300);

        Assert.Equal([1UL, 2UL], seen.Select(r => r.IconId).Order());
        Assert.All(seen, r => Assert.Equal(PopupOutcome.TimedOut, r.Outcome));
    }
}
