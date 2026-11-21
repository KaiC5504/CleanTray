using System.Diagnostics;
using CleanTray.Core;

namespace CleanTray;

static class Log
{
    static readonly RotatingFileLog File = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanTray", "app.log"),
        1 << 20);

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {message}";
        Trace.WriteLine("CleanTray: " + line);
        File.Write(line);
    }

    /// <summary>Writes out what is queued; later lines are dropped.</summary>
    public static void Close() => File.Dispose();
}
