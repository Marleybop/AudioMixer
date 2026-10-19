using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioMixer.Core.Audio;

/// <summary>
/// Puts Sonar's virtual audio driver back from exported driver packages (pnputil /export-driver), e.g. after
/// SteelSeries GG was uninstalled. The driver is a root-enumerated device (ROOT\VEN_SSGG&amp;DEV_0001) that GG's
/// installer creates, so the device node is created first if it's missing (what devcon install does), then the
/// three signed packages are installed: the virtual audio device, its extension, and the Sonar APO.
/// Needs administrator rights.
/// </summary>
public static class SonarDriverInstaller
{
    public const string HardwareId = @"ROOT\VEN_SSGG&DEV_0001";
    static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");

    /// <summary>Installs from a folder holding the exported packages (one folder per package); returns a log.</summary>
    public static string Install(string backupFolder)
    {
        var log = new StringBuilder();
        string Find(string inf) => Directory.EnumerateFiles(backupFolder, inf, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"{inf} not found under {backupFolder}");
        var vad = Find("steelseries-sonar-vad.inf");
        var extension = Find("steelseries-sonar-vad-extension.inf");
        var apo = Find("sonar.apo.inf");

        // Stage the APO and the extension first so they're there when the device starts.
        log.AppendLine(PnpUtil($"/add-driver \"{apo}\" /install"));
        log.AppendLine(PnpUtil($"/add-driver \"{extension}\" /install"));

        if (DevicePresent())
        {
            log.AppendLine("Virtual audio device already exists.");
        }
        else
        {
            CreateRootDevice();
            log.AppendLine($"Created device {HardwareId}.");
        }
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HardwareId, vad, InstallFlagForce, out var reboot))
            throw new InvalidOperationException($"Installing the virtual audio driver failed (error {Marshal.GetLastWin32Error()}).");
        log.AppendLine($"Installed the virtual audio driver{(reboot ? " (Windows wants a restart)" : "")}.");
        log.AppendLine(PnpUtil("/scan-devices"));
        return log.ToString();
    }

    static bool DevicePresent()
    {
        var output = PnpUtil("/enum-devices /class MEDIA /ids");
        return output.Contains(HardwareId, StringComparison.OrdinalIgnoreCase);
    }

    static string PnpUtil(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("pnputil.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output.Trim();
    }

    static void CreateRootDevice()
    {
        var classGuid = MediaClass;
        var set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == new IntPtr(-1))
            throw new InvalidOperationException($"SetupDiCreateDeviceInfoList failed ({Marshal.GetLastWin32Error()}).");
        try
        {
            var data = new SpDevinfoData { Size = (uint)Marshal.SizeOf<SpDevinfoData>() };
            if (!SetupDiCreateDeviceInfoW(set, "MEDIA", ref classGuid, null, IntPtr.Zero, DicdGenerateId, ref data))
                throw new InvalidOperationException($"SetupDiCreateDeviceInfo failed ({Marshal.GetLastWin32Error()}).");
            var ids = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SpdrpHardwareId, ids, (uint)ids.Length))
                throw new InvalidOperationException($"Setting the hardware id failed ({Marshal.GetLastWin32Error()}).");
            if (!SetupDiCallClassInstaller(DifRegisterDevice, set, ref data))
                throw new InvalidOperationException($"Registering the device failed ({Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    const uint DicdGenerateId = 1;
    const uint SpdrpHardwareId = 1;
    const uint DifRegisterDevice = 0x19;
    const uint InstallFlagForce = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct SpDevinfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string? description, IntPtr hwnd, uint flags, ref SpDevinfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SpDevinfoData data, uint property, byte[] buffer, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref SpDevinfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hardwareId, string infPath, uint flags, out bool rebootRequired);
}
