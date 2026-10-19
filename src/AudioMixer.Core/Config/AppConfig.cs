using System.Text.Json;
using System.Text.Json.Serialization;
using AudioMixer.Core.Sonar;

namespace AudioMixer.Core.Config;

/// <summary>A device remembered by id, with its name so the UI can show it while it is unplugged.</summary>
public sealed record DeviceRef(string Id, string Name);

public sealed class AppConfig
{
    /// <summary>Physical outputs in order of preference. The first one that is available is used.</summary>
    public List<DeviceRef> OutputPriority { get; set; } = [];

    /// <summary>Physical microphones in order of preference.</summary>
    public List<DeviceRef> InputPriority { get; set; } = [];

    /// <summary>Endpoints that are kept disabled so apps can't grab them (e.g. a game controller's speaker/mic).</summary>
    public List<DeviceRef> Blocked { get; set; } = [];

    /// <summary>Point Windows' default devices at the virtual channels (or, without Sonar, at the active devices).</summary>
    public bool ManageWindowsDefaults { get; set; } = true;

    /// <summary>Which virtual channel Windows' default playback device should be.</summary>
    public Channel DefaultPlaybackChannel { get; set; } = Channel.Game;

    /// <summary>Use the base station dial to balance Chat against Game.</summary>
    public bool ChatMixEnabled { get; set; } = true;

    /// <summary>Channel strips shown in the mixer, in order.</summary>
    public List<Channel> MixerChannels { get; set; } = [Channel.Game, Channel.Chat, Channel.Media, Channel.Mic];

    public HeadsetConfig Headset { get; set; } = new();

    /// <summary>Per-channel sound and device choices. Missing channels are filled in on first use.</summary>
    public Dictionary<Channel, ChannelConfig> Channels { get; set; } = [];

    public ChannelConfig ForChannel(Channel channel)
    {
        if (!Channels.TryGetValue(channel, out var c))
            Channels[channel] = c = new ChannelConfig();
        return c;
    }

    // ---- Persistence ----

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioMixer", "config.json");

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Json) ?? new AppConfig();
        }
        catch (JsonException)
        {
            // Corrupt file: keep a copy for inspection and start fresh rather than refusing to run.
            File.Copy(path, path + ".bad", overwrite: true);
        }
        return new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

public sealed class ChannelConfig
{
    /// <summary>A device this channel always uses (render id; capture id for the mic). Null = follow the priority list.</summary>
    public DeviceRef? Device { get; set; }

    /// <summary>The channel's EQ and effects. Null until first read from the APO (carries over what Sonar had).</summary>
    public SoundSettings? Sound { get; set; }

    /// <summary>The preset the sound came from, shown on the channel strip.</summary>
    public string PresetName { get; set; } = "Custom";

    /// <summary>True once the sound has been edited since the preset was picked.</summary>
    public bool Modified { get; set; }
}

public sealed class HeadsetConfig
{
    /// <summary>Notify when the headset battery drops to or below this percent (0 = off).</summary>
    public int LowBatteryWarningPercent { get; set; } = 25;

    /// <summary>What the base station's screen shows: BaseStation (its own), Widget (AudioMixer's) or Off.</summary>
    public string Screen { get; set; } = "BaseStation";

    /// <summary>The picture or GIF (file name in the Screens folder) shown when <see cref="Screen"/> is Custom.</summary>
    public string? ScreenImage { get; set; }

    /// <summary>Blank the base station's screen after this many minutes without keyboard, mouse or dial use (0 = never).</summary>
    public int ScreenSleepMinutes { get; set; }
}
