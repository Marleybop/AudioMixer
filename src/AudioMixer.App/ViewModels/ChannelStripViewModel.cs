using System.Windows.Media;
using AudioMixer.Core.Audio;
using AudioMixer.Core.Sonar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixer.App.ViewModels;

/// <summary>One channel strip in the mixer: volume, mute, live level and the apps playing on it.</summary>
public sealed partial class ChannelStripViewModel : ObservableObject
{
    static readonly Dictionary<Channel, System.Windows.Media.Color> ChannelColors = new()
    {
        [Channel.Game] = System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x3D),
        [Channel.Chat] = System.Windows.Media.Color.FromRgb(0x4F, 0xB3, 0xA9),
        [Channel.Media] = System.Windows.Media.Color.FromRgb(0x9D, 0x8C, 0xF0),
        [Channel.Aux] = System.Windows.Media.Color.FromRgb(0xC9, 0x7B, 0x5A),
        [Channel.Mic] = System.Windows.Media.Color.FromRgb(0xD9, 0xD6, 0xCF),
    };

    readonly Func<float?> _getGain;
    readonly Action<float> _setGain;
    readonly Func<bool> _getMute;
    readonly Action<bool> _setMute;
    readonly bool _squareLaw;
    bool _loading;

    public Channel Channel { get; }
    public string Name { get; }
    public Brush Color { get; }
    public bool IsMic => Channel == Channel.Mic;

    /// <summary>Bar position 0–100.</summary>
    [ObservableProperty] double _volume;
    [ObservableProperty] bool _muted;
    /// <summary>Live peak 0–100.</summary>
    [ObservableProperty] double _peak;
    [ObservableProperty] string _apps = "";

    /// <summary>The channel's EQ and effects (null when the Sonar engine isn't available).</summary>
    public ChannelSoundViewModel? Sound { get; set; }

    /// <summary>Devices this channel can be pinned to; the first entry is "Automatic" (follow the priority list).</summary>
    [ObservableProperty] IReadOnlyList<DeviceChoice> _deviceChoices = [];
    [ObservableProperty] DeviceChoice? _selectedDevice;
    /// <summary>Where the channel actually plays (or, for the mic, which mic feeds it).</summary>
    [ObservableProperty] string _deviceName = "";

    /// <summary>Raised when you pick a different device for this channel.</summary>
    public event EventHandler? DeviceChosen;

    partial void OnSelectedDeviceChanged(DeviceChoice? value)
    {
        if (!_loading && value is not null)
            DeviceChosen?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a device without raising <see cref="DeviceChosen"/>.</summary>
    public void ShowDevice(DeviceChoice? choice)
    {
        _loading = true;
        SelectedDevice = choice;
        _loading = false;
    }

    ChannelStripViewModel(Channel channel, string name, Func<float?> getGain, Action<float> setGain,
        Func<bool> getMute, Action<bool> setMute, bool squareLaw)
    {
        Channel = channel;
        Name = name;
        _getGain = getGain;
        _setGain = setGain;
        _getMute = getMute;
        _setMute = setMute;
        _squareLaw = squareLaw;
        var brush = new SolidColorBrush(ChannelColors[channel]);
        brush.Freeze();
        Color = brush;
        Refresh();
    }

    /// <summary>
    /// A Sonar output channel. Its volume is the APO redirection gain, mapped with a square law so the bar
    /// feels even to the ear.
    /// </summary>
    public static System.Windows.Media.Color ColorOf(Channel channel) => ChannelColors[channel];

    public static ChannelStripViewModel ForSonar(SonarEngine sonar, Channel channel, string name) => new(channel, name,
        () => sonar.GetVolume(channel), v => sonar.SetVolume(channel, v),
        () => sonar.GetMute(channel), m => sonar.SetMute(channel, m), squareLaw: true);

    /// <summary>The virtual mic that apps record from: Windows' own volume and mute of that device.</summary>
    public static ChannelStripViewModel ForMic(ChannelMonitor monitor) => new(Channel.Mic, "Mic",
        () => monitor.MicVolume, v => monitor.MicVolume = v,
        () => monitor.MicMuted, m => monitor.MicMuted = m, squareLaw: false);

    public void Refresh()
    {
        _loading = true;
        try
        {
            if (_getGain() is { } gain)
                Volume = Math.Round((_squareLaw ? Math.Sqrt(gain) : gain) * 100);
            Muted = _getMute();
        }
        catch (Exception)
        {
        }
        finally
        {
            _loading = false;
        }
    }


    partial void OnVolumeChanged(double value)
    {
        if (_loading)
            return;
        try { _setGain((float)(_squareLaw ? Math.Pow(value / 100, 2) : value / 100)); }
        catch (Exception) { }
    }

    partial void OnMutedChanged(bool value)
    {
        if (_loading)
            return;
        try { _setMute(value); }
        catch (Exception) { }
    }

}

/// <summary>An entry in a channel's device picker. <see cref="Id"/> null = Automatic.</summary>
public sealed partial class DeviceChoice(string? id, string label) : ObservableObject
{
    public string? Id { get; } = id;
    [ObservableProperty] string _label = label;
    public bool IsAutomatic => Id is null;
}
