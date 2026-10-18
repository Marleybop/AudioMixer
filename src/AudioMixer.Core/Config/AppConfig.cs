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

    /// <summary>Use the base station dial to balance chat against the other channels.</summary>
    public bool ChatMixEnabled { get; set; } = true;

    /// <summary>Channels that fade out as the dial moves toward chat (Sonar itself only fades Game).</summary>
    public List<Channel> ChatMixFadedChannels { get; set; } = [Channel.Game, Channel.Media, Channel.Aux];

    public HeadsetConfig Headset { get; set; } = new();

    // ---- Persistence ----

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
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

public sealed class HeadsetConfig
{
    /// <summary>Notify when the headset battery drops to or below this percent (0 = off).</summary>
    public int LowBatteryWarningPercent { get; set; } = 25;
}
