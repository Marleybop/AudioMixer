using System.Windows.Media;
using AudioMixer.Core.Sonar;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixer.App.ViewModels;

/// <summary>Whether a channel strip is shown in the mixer.</summary>
public sealed partial class ChannelToggleViewModel(ChannelStripViewModel strip, bool shown, Action changed) : ObservableObject
{
    public ChannelStripViewModel Strip { get; } = strip;
    public Channel Channel => Strip.Channel;
    public string Name => Strip.Name;
    public Brush Color => Strip.Color;

    [ObservableProperty] bool _isShown = shown;

    partial void OnIsShownChanged(bool value) => changed();
}
