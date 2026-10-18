using AudioMixer.Core.Audio;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioMixer.App.ViewModels;

/// <summary>A physical device in a priority list.</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] string _name;
    [ObservableProperty] EndpointState? _state; // null = not present on this PC right now
    /// <summary>Included in the priority list (unticked devices are never used automatically).</summary>
    [ObservableProperty] bool _enabled;
    [ObservableProperty] bool _blocked;
    [ObservableProperty] bool _isCurrent;

    public DeviceItemViewModel(string id, string name)
    {
        Id = id;
        _name = name;
    }

    public string StateText => State switch
    {
        null => "Not connected",
        EndpointState.Active => IsCurrent ? "In use" : "Available",
        EndpointState.Disabled => Blocked ? "Blocked" : "Disabled in Windows",
        EndpointState.Unplugged => "Unplugged",
        _ => "Not present",
    };

    partial void OnStateChanged(EndpointState? value) => OnPropertyChanged(nameof(StateText));
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(StateText));
    partial void OnBlockedChanged(bool value) => OnPropertyChanged(nameof(StateText));
}
