using System.Text.Json;
using AudioMixer.Core.Sonar;

namespace AudioMixer.Core.Config;

/// <summary>Presets you saved, kept in %APPDATA%\AudioMixer\presets.json (same JSON shape as Sonar's).</summary>
public sealed class PresetStore
{
    sealed record Entry(string Name, Channel Channel, SoundSettings Settings);

    static readonly JsonSerializerOptions Json = new(SoundSettings.Json) { WriteIndented = true };

    readonly string _path;
    List<Entry> _entries = [];

    public PresetStore(string? path = null)
    {
        _path = path ?? Path.Combine(Path.GetDirectoryName(AppConfig.DefaultPath)!, "presets.json");
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (JsonException)
        {
            File.Copy(_path, _path + ".bad", overwrite: true);
        }
    }

    /// <summary>Your presets that fit a channel: voice presets fit Chat and Mic, playback presets the other three.</summary>
    public IEnumerable<SoundPreset> For(Channel channel) => _entries
        .Where(e => SoundSettings.IsVoice(e.Channel) == SoundSettings.IsVoice(channel))
        .Select(e => new SoundPreset(e.Name, e.Channel, e.Settings.Clone(), FromSonar: false));

    /// <summary>Saves (or replaces, by name and kind) a preset.</summary>
    public void Save(string name, Channel channel, SoundSettings settings)
    {
        _entries.RemoveAll(e => Matches(e, name, channel));
        _entries.Add(new Entry(name, channel, settings.Clone()));
        Write();
    }

    public void Delete(string name, Channel channel)
    {
        if (_entries.RemoveAll(e => Matches(e, name, channel)) > 0)
            Write();
    }

    static bool Matches(Entry e, string name, Channel channel) =>
        string.Equals(e.Name, name, StringComparison.CurrentCultureIgnoreCase) && SoundSettings.IsVoice(e.Channel) == SoundSettings.IsVoice(channel);

    void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
