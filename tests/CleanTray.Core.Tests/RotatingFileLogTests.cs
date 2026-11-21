namespace CleanTray.Core.Tests;

public sealed class RotatingFileLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "CleanTrayTests-" + Guid.NewGuid().ToString("N"));
    string LogPath => Path.Combine(_dir, "sub", "app.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Write_AppendsLinesInOrder()
    {
        using var log = new RotatingFileLog(LogPath, 1024);
        log.Write("one");
        log.Write("two");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["one", "two"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Write_RotatesWhenTheNextLineWouldPassTheCap()
    {
        using var log = new RotatingFileLog(LogPath, 20);
        log.Write("0123456789");
        log.Write("abcdefghij");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["0123456789"], File.ReadAllLines(LogPath + ".1"));
        Assert.Equal(["abcdefghij"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Write_RotationReplacesTheOlderBackup()
    {
        using var log = new RotatingFileLog(LogPath, 15);
        log.Write("first-line");
        log.Write("second-line");
        log.Write("third-line");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(["second-line"], File.ReadAllLines(LogPath + ".1"));
        Assert.Equal(["third-line"], File.ReadAllLines(LogPath));
    }

    [Fact]
    public void Write_SurvivesAFailureThatIsNotIO()
    {
        // A NUL in the path throws ArgumentException, not IOException.
        using var log = new RotatingFileLog(Path.Combine(_dir, "bad\0name.log"), 1024);
        log.Write("lost");

        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
        log.Write("still alive");
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Write_AfterDisposeIsIgnored()
    {
        var log = new RotatingFileLog(LogPath, 1024);
        log.Write("kept");
        log.Dispose();
        log.Write("dropped");

        Assert.Equal(["kept"], File.ReadAllLines(LogPath));
    }
}
