using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace AudioMixer.Core.Stats;

/// <summary>
/// CPU temperature. Reading it needs a kernel driver and admin rights: LibreHardwareMonitorLib (MPL-2.0) with the
/// PawnIO driver, which the user installs themselves (pawnio.eu; it's GPL, so it isn't bundled). So the reading
/// happens in a small elevated copy of AudioMixer (<c>AudioMixer.exe --sensors</c>), started through a scheduled
/// task that is set up once with one admin prompt. It sends the temperature once a second over a local pipe and
/// quits when nothing has listened for a while.
/// The task runs a copy of the app under Program Files, never the app where the user keeps it: anything running as
/// the user can change files there, and the task would run them as administrator without asking.
/// </summary>
public static class CpuTemperature
{
    public const string PipeName = "AudioMixer.Sensors";
    public const string TaskName = "AudioMixer Sensors";
    public const string PawnIOUrl = "https://pawnio.eu/";

    /// <summary>Where the task's copy of the app lives; only administrators can change it.</summary>
    public static string InstallFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AudioMixer", "Sensors");

    public static bool PawnIOInstalled
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return key is not null;
        }
    }

    // ---- The elevated side ----

    /// <summary>
    /// Reads the CPU temperature and serves it on the pipe until nobody has been connected for <paramref name="idleExit"/>.
    /// Must run as administrator. Returns at once if another copy is already serving.
    /// </summary>
    public static void Serve(TimeSpan idleExit)
    {
        using var single = new Mutex(true, @"Local\" + PipeName, out var first);
        if (!first)
            return;
        var computer = new Computer { IsCpuEnabled = true };
        computer.Open();
        try
        {
            var line = "cpu=";
            var clients = 0;
            var lastClient = DateTime.UtcNow;
            var security = new PipeSecurity();
            // Anyone signed in may read a temperature; the elevated owner may create more pipe instances.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

            new Thread(() =>
            {
                while (true)
                {
                    var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
                    server.WaitForConnection();
                    Interlocked.Increment(ref clients);
                    new Thread(() =>
                    {
                        try
                        {
                            using var writer = new StreamWriter(server) { AutoFlush = true };
                            while (true)
                            {
                                writer.WriteLine(Volatile.Read(ref line));
                                Thread.Sleep(1000);
                            }
                        }
                        catch (IOException) { } // the client went away
                        finally
                        {
                            server.Dispose();
                            lastClient = DateTime.UtcNow;
                            Interlocked.Decrement(ref clients);
                        }
                    }) { IsBackground = true }.Start();
                }
            }) { IsBackground = true, Name = "Sensor pipe" }.Start();

            while (Volatile.Read(ref clients) > 0 || DateTime.UtcNow - lastClient < idleExit)
            {
                var temp = Read(computer);
                Volatile.Write(ref line, "cpu=" + (temp?.ToString("0.0", CultureInfo.InvariantCulture) ?? ""));
                Thread.Sleep(1000);
            }
        }
        finally
        {
            computer.Close();
        }
    }

    /// <summary>The CPU's own control temperature: Tctl/Tdie on AMD, the package on Intel, else the hottest sensor.</summary>
    static double? Read(Computer computer)
    {
        foreach (var cpu in computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu))
        {
            cpu.Update();
            var temps = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
            var pick = temps.FirstOrDefault(s => s.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                       ?? temps.FirstOrDefault(s => s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                       ?? temps.MaxBy(s => s.Value);
            if (pick?.Value is { } value)
                return value;
        }
        return null;
    }

    // ---- The scheduled task ----

    /// <summary>The program the task runs, or null if the task isn't set up.</summary>
    public static string? TaskCommand()
    {
        var (code, output) = Schtasks($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0)
            return null;
        var start = output.IndexOf("<Command>", StringComparison.Ordinal);
        var end = output.IndexOf("</Command>", StringComparison.Ordinal);
        return start < 0 || end < start ? null : WebUtility.HtmlDecode(output[(start + 9)..end]).Trim();
    }

    /// <summary>
    /// The files that make up the app at <paramref name="exePath"/>: just the exe for a single-file build (which may sit
    /// in Downloads next to anything), or its whole build folder when the app's DLL is next to it.
    /// </summary>
    static List<string> AppFiles(string exePath)
    {
        var folder = Path.GetDirectoryName(exePath)!;
        return File.Exists(Path.ChangeExtension(exePath, ".dll"))
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList()
            : [exePath];
    }

    /// <summary>True if the task is set up and its copy of the app matches the app at <paramref name="exePath"/>.</summary>
    public static bool TaskIsCurrent(string exePath)
    {
        if (!string.Equals(TaskCommand(), Path.Combine(InstallFolder, Path.GetFileName(exePath)), StringComparison.OrdinalIgnoreCase))
            return false;
        var folder = Path.GetDirectoryName(exePath)!;
        return AppFiles(exePath)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".exe" or ".json")
            .All(f => SameFile(f, Path.Combine(InstallFolder, Path.GetRelativePath(folder, f))));
    }

    static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(b) || new FileInfo(a).Length != new FileInfo(b).Length)
                return false;
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(fa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(fb));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the app at <paramref name="exePath"/> to <see cref="InstallFolder"/> and sets up the task that runs
    /// that copy elevated with <paramref name="arguments"/>, on demand only. Run as administrator. False if it couldn't.
    /// </summary>
    public static bool InstallTask(string exePath, string arguments)
    {
        try
        {
            StopReader();
            var folder = Path.GetDirectoryName(exePath)!;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(InstallFolder))
                        Directory.Delete(InstallFolder, recursive: true);
                    foreach (var source in AppFiles(exePath))
                    {
                        var target = Path.Combine(InstallFolder, Path.GetRelativePath(folder, source));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target);
                    }
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
                {
                    Thread.Sleep(500); // the old reader is still closing (its files stay locked until it has)
                }
            }
            return CreateTask(exePath, arguments);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool CreateTask(string exePath, string arguments)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Reads the CPU temperature for AudioMixer's base station screen. AudioMixer starts it; it stops by itself when AudioMixer isn't listening.</Description>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>{WindowsIdentity.GetCurrent().User}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(Path.Combine(InstallFolder, Path.GetFileName(exePath)))}</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "AudioMixer.Sensors.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try { return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F").Code == 0; }
        finally { File.Delete(file); }
    }

    /// <summary>Removes the task and its copy of the app. Run as administrator.</summary>
    public static bool RemoveTask()
    {
        StopReader();
        var removed = Schtasks($"/Delete /TN \"{TaskName}\" /F").Code == 0;
        for (var attempt = 0; attempt < 10 && Directory.Exists(InstallFolder); attempt++)
        {
            try { Directory.Delete(InstallFolder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(500); }
        }
        return removed;
    }

    /// <summary>
    /// Stops the reader so its files can be replaced: disables the task first, or a running AudioMixer would start it
    /// again within seconds, then ends it and anything still running from <see cref="InstallFolder"/>.
    /// </summary>
    static void StopReader()
    {
        Schtasks($"/Change /TN \"{TaskName}\" /Disable");
        Schtasks($"/End /TN \"{TaskName}\"");
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(InstallFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception) { } // processes we can't open (system, other users) aren't ours
            }
        }
    }

    /// <summary>Starts the elevated reader through the task (no prompt).</summary>
    public static bool StartTask() => Schtasks($"/Run /TN \"{TaskName}\"").Code == 0;

    static (int Code, string Output) Schtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        catch (Exception e)
        {
            return (-1, e.Message);
        }
    }
}

/// <summary>Listens to the elevated reader. <see cref="Value"/> is null when there's no fresh reading.</summary>
public sealed class CpuTemperatureClient : IDisposable
{
    volatile bool _disposed;
    double? _value;
    DateTime _at;
    DateTime _startedHelper = DateTime.MinValue;

    public CpuTemperatureClient()
    {
        new Thread(Listen) { IsBackground = true, Name = "CPU temperature" }.Start();
    }

    /// <summary>True while connected to the reader.</summary>
    public bool Connected { get; private set; }

    public double? Value
    {
        get
        {
            lock (this)
                return DateTime.UtcNow - _at < TimeSpan.FromSeconds(5) ? _value : null;
        }
    }

    void Listen()
    {
        while (!_disposed)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", CpuTemperature.PipeName, PipeDirection.In);
                pipe.Connect(1500);
                Connected = true;
                using var reader = new StreamReader(pipe);
                while (!_disposed && reader.ReadLine() is { } line)
                {
                    if (!line.StartsWith("cpu=", StringComparison.Ordinal))
                        continue;
                    lock (this)
                    {
                        _value = double.TryParse(line[4..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
                        _at = DateTime.UtcNow;
                    }
                }
            }
            catch (TimeoutException)
            {
                // Not running: start it through the task, but not more than once every 30 s.
                if (DateTime.UtcNow - _startedHelper > TimeSpan.FromSeconds(30))
                {
                    _startedHelper = DateTime.UtcNow;
                    CpuTemperature.StartTask();
                }
            }
            catch (Exception) { }
            Connected = false;
            if (!_disposed)
                Thread.Sleep(1000);
        }
    }

    public void Dispose() => _disposed = true;
}
