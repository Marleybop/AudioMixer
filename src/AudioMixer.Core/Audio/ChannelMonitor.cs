using System.Diagnostics;
using AudioMixer.Core.Sonar;
using NAudio.CoreAudioApi;

namespace AudioMixer.Core.Audio;

/// <summary>
/// Live view of the Sonar virtual channels for the UI: each channel's peak level, the apps playing on it,
/// and the volume and mute of the virtual microphone that apps record from.
/// Holds COM objects, so create and use it on one thread (the UI thread). Call <see cref="Refresh"/> after
/// device changes.
/// </summary>
public sealed class ChannelMonitor : IDisposable
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly Dictionary<Channel, MMDevice> _render = [];
    readonly Dictionary<int, string> _processNames = [];
    MMDevice? _micCapture;

    public ChannelMonitor() => Refresh();

    /// <summary>Re-finds the virtual endpoints (they can be disabled, re-enabled or reinstalled).</summary>
    public void Refresh()
    {
        ReleaseDevices();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
        {
            Channel? channel;
            try
            {
                channel = new AudioEndpoint(device.ID, device.FriendlyName, device.DeviceFriendlyName,
                    EndpointFlow.Output, EndpointState.Active).SonarChannel;
            }
            catch (Exception)
            {
                channel = null;
            }

            if (channel is null)
                device.Dispose();
            else if (device.DataFlow == DataFlow.Capture && channel == Channel.Mic && _micCapture is null)
                _micCapture = device;
            else if (device.DataFlow == DataFlow.Render && !_render.ContainsKey(channel.Value))
                _render[channel.Value] = device;
            else
                device.Dispose();
        }
    }

    /// <summary>
    /// Current peak 0…1 of what is playing into a channel. For <see cref="Channel.Mic"/> this is the signal the
    /// mic bridge feeds into the virtual mic.
    /// </summary>
    public float GetPeak(Channel channel)
    {
        try { return _render.TryGetValue(channel, out var d) ? d.AudioMeterInformation.MasterPeakValue : 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>Names of the apps that currently have an active audio session on a channel.</summary>
    public IReadOnlyList<string> GetApps(Channel channel)
    {
        if (!_render.TryGetValue(channel, out var device))
            return [];
        var names = new List<string>();
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();
            var sessions = manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive)
                    continue;
                var pid = (int)session.GetProcessID;
                if (pid == Environment.ProcessId)
                    continue; // our own mic bridge
                var name = ProcessName(pid);
                if (name is not null && !names.Contains(name))
                    names.Add(name);
            }
        }
        catch (Exception)
        {
            // Sessions come and go while we read them.
        }
        return names;
    }

    /// <summary>Volume 0…1 of the virtual mic (what Discord and games record), or null if it isn't there.</summary>
    public float? MicVolume
    {
        get
        {
            try { return _micCapture?.AudioEndpointVolume.MasterVolumeLevelScalar; }
            catch (Exception) { return null; }
        }
        set
        {
            if (_micCapture is not null && value is { } v)
                _micCapture.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f);
        }
    }

    /// <summary>Software mute of the virtual mic. Separate from the headset's own mic mute switch.</summary>
    public bool MicMuted
    {
        get
        {
            try { return _micCapture?.AudioEndpointVolume.Mute == true; }
            catch (Exception) { return false; }
        }
        set
        {
            if (_micCapture is not null)
                _micCapture.AudioEndpointVolume.Mute = value;
        }
    }

    string? ProcessName(int pid)
    {
        if (_processNames.TryGetValue(pid, out var cached))
            return cached;
        string? name = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            try { name = process.MainModule?.FileVersionInfo.FileDescription; }
            catch (Exception) { } // other users' and elevated processes deny module access
            if (string.IsNullOrWhiteSpace(name))
                name = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null; // already exited
        }
        _processNames[pid] = name;
        return name;
    }

    void ReleaseDevices()
    {
        foreach (var device in _render.Values)
            device.Dispose();
        _render.Clear();
        _micCapture?.Dispose();
        _micCapture = null;
    }

    public void Dispose()
    {
        ReleaseDevices();
        _enumerator.Dispose();
    }
}
