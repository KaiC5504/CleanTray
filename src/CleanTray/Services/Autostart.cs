using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using CleanTray.Core;
using Microsoft.Win32;

namespace CleanTray.Services;

enum AutostartMethod
{
    ScheduledTask,
    RunKey,
}

/// <summary>
/// Starts CleanTray at logon. A scheduled task is preferred because it can start at once with no
/// startup delay; the Run key is the fallback when the task cannot be created.
/// </summary>
static class Autostart
{
    const string TaskName = "CleanTray";
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(15);

    public static AutostartMethod Enable(string exePath)
    {
        var xml = Path.Combine(Path.GetTempPath(), $"CleanTray-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks reads the XML as UTF-16.
            File.WriteAllText(xml, AutostartTask.Xml(WindowsIdentity.GetCurrent().Name, exePath), Encoding.Unicode);
            var (exit, _) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F");
            if (exit == 0)
            {
                DeleteRunValue();
                return AutostartMethod.ScheduledTask;
            }
            Log.Write($"autostart: schtasks /Create exited {exit}, using the Run key");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"autostart: cannot write the task file ({LogText.Error(e)}), using the Run key");
        }
        finally
        {
            try
            {
                File.Delete(xml);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(TaskName, "\"" + exePath + "\"");
        return AutostartMethod.RunKey;
    }

    public static void Disable()
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        DeleteRunValue();
    }

    public static bool IsEnabled()
    {
        if (RunSchtasks($"/Query /TN \"{TaskName}\"").Exit == 0) return true;
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(TaskName) is not null;
    }

    /// <summary>True when the registered task or Run entry starts <paramref name="exePath"/>, so a moved install gets re-registered.</summary>
    public static bool IsCurrent(string exePath)
    {
        var (exit, xml) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (exit == 0) return SamePath(AutostartTask.CommandPath(xml), exePath);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(TaskName) is string command && SamePath(command.Trim().Trim('"'), exePath);
    }

    static bool SamePath(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(TaskName, throwOnMissingValue: false);
    }

    /// <summary>Exit code of schtasks (-1 when it could not run or did not finish in time) and its standard output.</summary>
    static (int Exit, string Output) RunSchtasks(string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, "");
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                lock (output) output.AppendLine(e.Data);
            };
            // Drained so a full pipe never blocks schtasks.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                process.Kill();
                return (-1, "");
            }
            // The parameterless overload waits for the redirected streams to finish.
            process.WaitForExit();
            lock (output) return (process.ExitCode, output.ToString());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "");
        }
    }
}
