using AudioMixer.Core.Sonar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixer.App.ViewModels;

/// <summary>One Sonar channel in the mixer: volume and mute of its redirection to the active output.</summary>
public sealed partial class ChannelStripViewModel : ObservableObject
{
    readonly SonarEngine _sonar;
    bool _loading;

    public Channel Channel { get; }
    public string Name { get; }

    /// <summary>Slider position 0–100. Mapped to gain with a square law so the slider feels even to the ear.</summary>
    [ObservableProperty] double _volume;
    [ObservableProperty] bool _muted;

    public ChannelStripViewModel(SonarEngine sonar, Channel channel, string name)
    {
        _sonar = sonar;
        Channel = channel;
        Name = name;
        Refresh();
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            Volume = Math.Round(Math.Sqrt(_sonar.GetVolume(Channel)) * 100);
            Muted = _sonar.GetMute(Channel);
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
        if (!_loading)
            _sonar.SetVolume(Channel, (float)Math.Pow(value / 100, 2));
    }

    partial void OnMutedChanged(bool value)
    {
        if (!_loading)
            _sonar.SetMute(Channel, value);
    }
}
