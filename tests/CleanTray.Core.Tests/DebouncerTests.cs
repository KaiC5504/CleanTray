namespace CleanTray.Core.Tests;

public sealed class DebouncerTests
{
    static readonly TimeSpan Window = TimeSpan.FromMilliseconds(60);

    [Fact]
    public void Request_BurstRunsTheActionOnce()
    {
        var runs = 0;
        using var done = new ManualResetEventSlim();
        using var d = new Debouncer(Window, () =>
        {
            Interlocked.Increment(ref runs);
            done.Set();
        });

        for (var i = 0; i < 20; i++) d.Request();

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(Window * 3);
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void Request_AfterTheWindowStartsANewOne()
    {
        var runs = 0;
        using var d = new Debouncer(Window, () => Interlocked.Increment(ref runs));

        d.Request();
        SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 1, TimeSpan.FromSeconds(5));
        d.Request();
        SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 2, TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public void IsPending_TracksTheWindow()
    {
        using var d = new Debouncer(TimeSpan.FromMilliseconds(100), () => { });

        Assert.False(d.IsPending);
        d.Request();
        Assert.True(d.IsPending);
        SpinWait.SpinUntil(() => !d.IsPending, TimeSpan.FromSeconds(5));
        Assert.False(d.IsPending);
    }

    [Fact]
    public void Cancel_DropsPendingWorkButAllowsLaterRequests()
    {
        var runs = 0;
        using var d = new Debouncer(Window, () => Interlocked.Increment(ref runs));

        d.Request();
        d.Cancel();
        Thread.Sleep(Window * 4);
        Assert.Equal(0, runs);

        d.Request();
        SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    [Fact]
    public void Dispose_DropsPendingWork()
    {
        var runs = 0;
        var d = new Debouncer(Window, () => Interlocked.Increment(ref runs));

        d.Request();
        d.Dispose();
        Thread.Sleep(Window * 4);

        Assert.Equal(0, runs);
        d.Request();
        Assert.False(d.IsPending);
    }
}
