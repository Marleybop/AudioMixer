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
    public const byte DrawBitmap = 0x93;    // feature report: draw an image on the OLED (see OledFrame)
    public const byte ReloadDisplay = 0x95; // back to the base station's own screen
    public const byte BluetoothStartup = 0xB2;
    public const byte BluetoothCallMode = 0xB3;
    public const byte MutedMicBrightness = 0xBF;
    public const byte PowerInactivityTimer = 0xC1;
    public const byte WirelessMode = 0xC3;

    // Pushed by the base station (sync interface, report 0x07) when something changes on the device
    public const byte VolumeEvent = 0x25;   // [2] = volume 0…56
    public const byte ChatMixEvent = 0x45;  // [2] = game 0…100, [3] = chat 0…100 (one of them is 100)
    public const byte ConnectionEvent = 0xB5;
    public const byte CustomEqBandEvent = 0x31; // [2] = band, [3] = gain byte

    /// <summary>Settings the base station echoes on the sync interface when they are changed on its own menu.</summary>
    public static readonly IReadOnlySet<byte> SettingEchoes = new HashSet<byte>
    {
        HighGain, EqPreset, CustomEqBandEvent, MicVolume, Sidetone, LineOutMode, StreamMix, DimTimer, OledBrightness,
        HomeScreenType, BluetoothStartup, BluetoothCallMode, MutedMicBrightness, PowerInactivityTimer, WirelessMode,
    };

    /// <summary>
    /// Every command this app may send. Anything else is refused, in particular reset (0x01),
    /// firmware flashing (0xA1), bootloader handover (0xBB) and reboot-to-bootloader (0xEB):
    /// those can brick a base station, and some units run a patched bootloader.
    /// </summary>
    public static readonly IReadOnlySet<byte> AllowedCommands = new HashSet<byte>
    {
        FirmwareVersions, UniqueId, AudioSettings, UxSettings, WirelessSettings, BatteryStatus,
        SaveToFlash, HighGain, EqPreset, CustomEq, MicVolume, Sidetone, LineOutMode, StreamMix, SoftwareChatMix,
        DimTimer, OledBrightness, HomeScreenType, SonarPresent, DrawBitmap, ReloadDisplay,
        BluetoothStartup, BluetoothCallMode, MutedMicBrightness, PowerInactivityTimer, WirelessMode,
    };

    /// <summary>EQ gain in dB (−10…+10, 0.5 dB steps) to the device byte (0…40, 20 = 0 dB).</summary>
    public static byte EqGainToByte(double db) => (byte)Math.Clamp((int)Math.Round((db + 10) * 2), 0, 40);

    public static double EqByteToGain(byte value) => value / 2.0 - 10;

    /// <summary>The timer steps the base station stores (dim timer, headset sleep), in minutes; 0 = never.</summary>
    public static readonly IReadOnlyList<int> TimerMinutes = [0, 1, 5, 10, 15, 30, 60];

    /// <summary>Stored timer step (0…6) to minutes.</summary>
    public static int TimerStepToMinutes(byte step) => step < TimerMinutes.Count ? TimerMinutes[step] : 0;

    /// <summary>
    /// Minutes to the step the base station takes. Both the writes (0x83, 0xC1) and the reports use the step;
    /// GG's engine converts minutes to the step before sending (spec: transform_minutes_to_*).
    /// </summary>
    public static byte MinutesToTimerStep(int minutes)
    {
        for (var i = 0; i < TimerMinutes.Count; i++)
            if (TimerMinutes[i] >= minutes)
                return (byte)i;
        return (byte)(TimerMinutes.Count - 1);
    }

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
    bool BluetoothOn,
    bool BluetoothOnAtStartup,
    BluetoothCallMode BluetoothCallMode,
    int MutedMicBrightness,
    int SleepAfterMinutes,
    bool RangeMode)
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
        BluetoothOn: r[4] != 1,
        BluetoothOnAtStartup: r[2] == 1,
        BluetoothCallMode: (BluetoothCallMode)r[3],
        MutedMicBrightness: r[11],
        SleepAfterMinutes: NovaProProtocol.TimerStepToMinutes(r[12]),
        RangeMode: r[13] == 1);
}

public enum BluetoothCallMode : byte
{
    DoNothing = 0,
    LowerOtherAudio = 1, // −12 dB
    MuteOtherAudio = 2,
}

public enum LineOutMode : byte
{
    ChatMix = 1,
    StreamMix = 2,
}

/// <summary>Audio settings stored in the base station (0x20 report).</summary>
public sealed record BaseAudioSettings(
    int Input,
    int Volume,
    bool HighGain,
    int EqPreset,
    double[] CustomEqDb,
    int MicVolume,
    int Sidetone,
    LineOutMode LineOut,
    int ChatMixGame,
    int ChatMixChat,
    int StreamMain,
    int StreamAux,
    int StreamMic)
{
    /// <summary>Highest value of <see cref="Volume"/> (the base station's volume dial).</summary>
    public const int MaxVolume = 56;

    public static BaseAudioSettings FromReport(byte[] r) => new(
        Input: r[2],
        Volume: r[3],
        HighGain: r[4] == 2,
        EqPreset: r[6],
        CustomEqDb: Enumerable.Range(0, 10).Select(i => NovaProProtocol.EqByteToGain(r[7 + i])).ToArray(),
        MicVolume: r[17],
        Sidetone: r[18],
        LineOut: (LineOutMode)r[19],
        ChatMixGame: r[20],
        ChatMixChat: r[21],
        // r[23] is unused
        StreamMain: r[22],
        StreamAux: r[24],
        StreamMic: r[25]);

    /// <summary>Which source the base station is playing (USB 1 / USB 2 / Xbox), as a short label.</summary>
    public string InputName => Input switch { 0 => "USB 1", 1 => "USB 2", 3 => "Xbox", _ => $"Input {Input}" };
}

/// <summary>Base station screen settings (0x80 report).</summary>
public sealed record BaseUxSettings(
    int DimAfterMinutes,
    int Brightness,
    int IdleBrightness,
    bool SimpleHomeScreen,
    bool HomeScreenMicMute,
    bool SonarPresent)
{
    public static BaseUxSettings FromReport(byte[] r) => new(
        DimAfterMinutes: NovaProProtocol.TimerStepToMinutes(r[2]),
        Brightness: r[3],
        IdleBrightness: r[4],
        SimpleHomeScreen: r[5] == 1,
        HomeScreenMicMute: r[6] == 1,
        SonarPresent: r[7] == 1);
}

/// <summary>Firmware of the base station (DSP, MCU) and the headset (MCU, Bluetooth), from the 0x10 report.</summary>
public sealed record FirmwareVersions(string BaseDsp, string BaseMcu, string HeadsetMcu, string HeadsetBluetooth)
{
    /// <summary>Four 12-character fields such as "0000.003.082"; shown as "0.3.82". All zeros = headset not paired.</summary>
    public static FirmwareVersions FromReport(byte[] r)
    {
        string Field(int i)
        {
            var text = System.Text.Encoding.ASCII.GetString(r, 2 + i * 12, 12).TrimEnd('\0', ' ');
            var parts = text.Split('.').Select(p => int.TryParse(p, out var n) ? n.ToString() : p).ToArray();
            return parts.All(p => p == "0") ? "" : string.Join('.', parts);
        }
        return new FirmwareVersions(Field(0), Field(1), Field(2), Field(3));
    }
}
