using System.Runtime.InteropServices;

namespace AudioMixer.Core.Audio;

public enum DeviceRole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>
/// Windows' internal audio policy interface (what the Sound control panel uses) to change default
/// devices and enable/disable endpoints. Undocumented but stable since Windows 7.
/// </summary>
public static class PolicyConfig
{
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient;

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        void Reserved_GetMixFormat();
        void Reserved_GetDeviceFormat();
        void Reserved_ResetDeviceFormat();
        void Reserved_SetDeviceFormat();
        void Reserved_GetProcessingPeriod();
        void Reserved_SetProcessingPeriod();
        void Reserved_GetShareMode();
        void Reserved_SetShareMode();
        void Reserved_GetPropertyValue();
        void Reserved_SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, DeviceRole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    static T WithClient<T>(Func<IPolicyConfig, T> action)
    {
        var client = (IPolicyConfig)new PolicyConfigClient();
        try { return action(client); }
        finally { Marshal.ReleaseComObject(client); }
    }

    public static void SetDefault(string deviceId, DeviceRole role) =>
        Marshal.ThrowExceptionForHR(WithClient(c => c.SetDefaultEndpoint(deviceId, role)));

    public static void SetDefaultForAllRoles(string deviceId)
    {
        foreach (var role in Enum.GetValues<DeviceRole>())
            SetDefault(deviceId, role);
    }

    /// <summary>Enables or disables an endpoint (same as Enable/Disable in the Sound control panel).</summary>
    public static void SetEnabled(string deviceId, bool enabled) =>
        Marshal.ThrowExceptionForHR(WithClient(c => c.SetEndpointVisibility(deviceId, enabled)));
}
