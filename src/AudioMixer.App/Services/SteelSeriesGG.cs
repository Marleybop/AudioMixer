using System.Diagnostics;
using Microsoft.Win32;

namespace AudioMixer.App.Services;

/// <summary>
/// Detects SteelSeries GG/Sonar and hands audio control over to us. GG's watchdog (SteelSeriesGGEZ) restarts its
/// apps within seconds, so it has to be stopped first. Its virtual audio driver stays installed: we use it.
/// </summary>
public static class SteelSeriesGG
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "SteelSeriesGG";
    const string DisabledRunValue = "SteelSeriesGG (disabled by AudioMixer)";

    /// <summary>True while Sonar's app is running (it would fight us over routing and the mic).</summary>
    public static bool IsSonarRunning => Process.GetProcessesByName("SteelSeriesSonar").Length > 0;

    public static bool IsAnyRunning => Process.GetProcesses().Any(p => p.ProcessName.StartsWith("SteelSeries", StringComparison.OrdinalIgnoreCase));

    /// <summary>Stops GG (watchdog first) so nothing restarts Sonar behind our back.</summary>
    public static void Stop()
    {
        foreach (var name in new[] { "SteelSeriesGGEZ", "SteelSeriesGG", "SteelSeriesSonar", "SteelSeriesEngine", "SteelSeriesPrism" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                try { process.Kill(); process.WaitForExit(3000); }
                catch (Exception) { }
            }
    }

    public static bool IsAutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
    }

    /// <summary>Stops GG launching at sign-in. The command is kept under another name so it can be restored.</summary>
    public static void DisableAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is string command)
        {
            key.SetValue(DisabledRunValue, command);
            key.DeleteValue(RunValue);
        }
    }

    public static void RestoreAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(DisabledRunValue) is string command)
        {
            key.SetValue(RunValue, command);
            key.DeleteValue(DisabledRunValue);
        }
    }
}
