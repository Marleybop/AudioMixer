namespace AudioMixer.Core.Headset;

/// <summary>
/// Arctis Nova Pro Wireless base station protocol (HID report 0x06, 64-byte reports).
/// Source: SteelSeries' device spec (base_arctis_nova_pro_wireless.lsp), verified against the live device.
/// </summary>
public static class NovaProProtocol
{
    public const int VendorId = 0x1038;
    public const int ProductId = 0x12E0;
    public const byte ReportId = 0x06;
    /// <summary>Report id the base station uses for pushes on the sync interface (MI_04 Col02).</summary>
    public const byte SyncReportId = 0x07;

    // Reads
    public const byte FirmwareVersions = 0x10;
    public const byte UniqueId = 0x12;
    public const byte AudioSettings = 0x20;
    public const byte UxSettings = 0x80;
    public const byte WirelessSettings = 0xB0;
    public const byte BatteryStatus = 0xB7;

    // Writes
    public const byte SaveToFlash = 0x09;
    public const byte HighGain = 0x27;
    public const byte EqPreset = 0x2E;
    public const byte CustomEq = 0x33;
    public const byte MicVolume = 0x37;
    public const byte Sidetone = 0x39;
    public const byte LineOutMode = 0x43;
    public const byte StreamMix = 0x47;
    public const byte SoftwareChatMix = 0x49;
    public const byte DimTimer = 0x83;
    public const byte OledBrightness = 0x85;
    public const byte HomeScreenType = 0x89;
    public const byte SonarPresent = 0x8D;
    public const byte ReloadDisplay = 0x95;
    public const byte BluetoothStartup = 0xB2;
    public const byte BluetoothCallMode = 0xB3;
    public const byte MutedMicBrightness = 0xBF;
    public const byte PowerInactivityTimer = 0xC1;
    public const byte WirelessMode = 0xC3;

    // Pushed by the base station (sync interface, report 0x07) when something changes on the device
    public const byte VolumeEvent = 0x25;   // [2] = volume 0…56
    public const byte ChatMixEvent = 0x45;  // [2] = game 0…100, [3] = chat 0…100 (one of them is 100)
    public const byte ConnectionEvent = 0xB5;

    /// <summary>
    /// Every command this app may send. Anything else is refused, in particular reset (0x01),
    /// firmware flashing (0xA1), bootloader handover (0xBB) and reboot-to-bootloader (0xEB):
    /// those can brick a base station, and some units run a patched bootloader.
    /// </summary>
    public static readonly IReadOnlySet<byte> AllowedCommands = new HashSet<byte>
    {
        FirmwareVersions, UniqueId, AudioSettings, UxSettings, WirelessSettings, BatteryStatus,
        SaveToFlash, HighGain, EqPreset, CustomEq, MicVolume, Sidetone, LineOutMode, StreamMix, SoftwareChatMix,
        DimTimer, OledBrightness, HomeScreenType, SonarPresent, ReloadDisplay,
        BluetoothStartup, BluetoothCallMode, MutedMicBrightness, PowerInactivityTimer, WirelessMode,
    };

    /// <summary>EQ gain in dB (−10…+10, 0.5 dB steps) to the device byte (0…40, 20 = 0 dB).</summary>
    public static byte EqGainToByte(double db) => (byte)Math.Clamp((int)Math.Round((db + 10) * 2), 0, 40);

    public static double EqByteToGain(byte value) => value / 2.0 - 10;

    /// <summary>Battery level byte (0…8) to percent, as the official software maps it.</summary>
    public static int BatteryPercent(byte level) => level switch
    {
        0 => 0, 1 => 12, 2 => 25, 3 => 37, 4 => 50, 5 => 62, 6 => 75, 7 => 87, 8 => 100, _ => 0,
    };
}

public enum RadioState : byte
{
    NotPairedNotSearching = 1,
    NotPairedSearching = 2,
    PairedNotConnected = 4,
    Connected = 8,
}

public enum ChargeState : byte
{
    Disconnected = 1,
    Charging = 2,
    Charged = 4,
    Discharging = 8,
}

public enum NoiseControl : byte
{
    Off = 0,
    Transparency = 1,
    Anc = 2,
}

/// <summary>Snapshot of the headset/base station state (from the 0xB0 wireless settings report).</summary>
public sealed record HeadsetStatus(
    RadioState Radio,
    ChargeState Charge,
    int HeadsetBatteryPercent,
    int SpareBatteryPercent,
    bool MicMuted,
    NoiseControl NoiseControl,
    int TransparencyLevel,
    bool BluetoothOn)
{
    public bool IsHeadsetOn => Radio == RadioState.Connected;

    public static HeadsetStatus FromWirelessSettings(ReadOnlySpan<byte> r) => new(
        Radio: (RadioState)r[14],
        Charge: (ChargeState)r[15],
        HeadsetBatteryPercent: NovaProProtocol.BatteryPercent(r[6]),
        SpareBatteryPercent: NovaProProtocol.BatteryPercent(r[7]),
        MicMuted: r[9] == 1,
        NoiseControl: (NoiseControl)r[10],
        TransparencyLevel: r[8],
        BluetoothOn: r[4] != 1);
}

/// <summary>Audio settings stored in the base station (0x20 report).</summary>
public sealed record BaseAudioSettings(
    int Volume,
    bool HighGain,
    int EqPreset,
    double[] CustomEqDb,
    int MicVolume,
    int Sidetone,
    int ChatMixGame,
    int ChatMixChat)
{
    public static BaseAudioSettings FromReport(byte[] r) => new(
        Volume: r[3],
        HighGain: r[4] == 2,
        EqPreset: r[6],
        CustomEqDb: Enumerable.Range(0, 10).Select(i => NovaProProtocol.EqByteToGain(r[7 + i])).ToArray(),
        MicVolume: r[17],
        Sidetone: r[18],
        ChatMixGame: r[20],
        ChatMixChat: r[21]);
}
