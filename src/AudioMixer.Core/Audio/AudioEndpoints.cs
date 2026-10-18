using AudioMixer.Core.Sonar;
using NAudio.CoreAudioApi;

namespace AudioMixer.Core.Audio;

public enum EndpointFlow
{
    Output,
    Input,
}

public enum EndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

/// <summary>A Windows audio endpoint, e.g. "Headphones (Arctis Nova Pro Wireless)".</summary>
public sealed record AudioEndpoint(
    string Id,
    string Name,
    string AdapterName,
    EndpointFlow Flow,
    EndpointState State)
{
    public string DisplayName => string.IsNullOrEmpty(AdapterName) ? Name : $"{Name} ({AdapterName})";

    /// <summary>The Sonar virtual channel this endpoint carries, or null for a physical device.</summary>
    public Channel? SonarChannel => !AdapterName.Contains("Sonar Virtual Audio Device", StringComparison.OrdinalIgnoreCase) ? null
        : Name.Contains("Gaming") ? Channel.Game
        : Name.Contains("Chat") ? Channel.Chat
        : Name.Contains("Media") ? Channel.Media
        : Name.Contains("Aux") ? Channel.Aux
        : Name.Contains("Microphone") ? Channel.Mic
        : null;

    public bool IsVirtual => AdapterName.Contains("Virtual", StringComparison.OrdinalIgnoreCase);

    public bool IsNovaProWireless => AdapterName.Contains("Arctis Nova Pro Wireless", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Enumerates audio endpoints and raises <see cref="Changed"/> when devices appear, disappear or change state.</summary>
public sealed class AudioEndpoints : IDisposable
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly MMDeviceNotificationClient _notifications;
    readonly Timer _debounce;

    /// <summary>Raised (debounced) after any endpoint is added, removed, enabled/disabled or plugged/unplugged.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when Windows' default device for a role changes (flow, role, device id).</summary>
    public event EventHandler<(EndpointFlow Flow, DeviceRole Role, string? DeviceId)>? DefaultChanged;

    public AudioEndpoints()
    {
        _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty));
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DeviceAdded += (_, _) => Poke();
        _notifications.DeviceRemoved += (_, _) => Poke();
        _notifications.DeviceStateChanged += (_, _) => Poke();
        _notifications.DefaultDeviceChanged += (_, e) => DefaultChanged?.Invoke(this,
            (e.Flow == DataFlow.Capture ? EndpointFlow.Input : EndpointFlow.Output, (DeviceRole)e.Role, e.DeviceId));
    }

    public IReadOnlyList<AudioEndpoint> GetAll()
    {
        var result = new List<AudioEndpoint>();
        var states = DeviceState.Active | DeviceState.Disabled | DeviceState.Unplugged;
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.All, states))
        {
            using (device)
            {
                try
                {
                    result.Add(new AudioEndpoint(
                        device.ID,
                        device.FriendlyName.Replace($" ({device.DeviceFriendlyName})", ""),
                        device.DeviceFriendlyName,
                        device.DataFlow == DataFlow.Capture ? EndpointFlow.Input : EndpointFlow.Output,
                        ToState(device.State)));
                }
                catch (Exception)
                {
                    // Endpoint vanished mid-enumeration.
                }
            }
        }
        return result;
    }

    public AudioEndpoint? Find(string id) => GetAll().FirstOrDefault(e => e.Id == id);

    public string? GetDefaultId(EndpointFlow flow, DeviceRole role)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(flow == EndpointFlow.Input ? DataFlow.Capture : DataFlow.Render, (Role)role);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal MMDevice GetDevice(string id) => _enumerator.GetDevice(id);

    static EndpointState ToState(DeviceState state) => state switch
    {
        DeviceState.Active => EndpointState.Active,
        DeviceState.Disabled => EndpointState.Disabled,
        DeviceState.Unplugged => EndpointState.Unplugged,
        _ => EndpointState.NotPresent,
    };

    void Poke() => _debounce.Change(300, Timeout.Infinite);

    public void Dispose()
    {
        _notifications.Dispose();
        _debounce.Dispose();
        _enumerator.Dispose();
    }
}
