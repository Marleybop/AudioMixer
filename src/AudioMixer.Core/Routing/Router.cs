using AudioMixer.Core.Audio;
using AudioMixer.Core.Config;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Sonar;

namespace AudioMixer.Core.Routing;

/// <summary>What the router decided and applied on its last pass.</summary>
public sealed record RouteState(
    AudioEndpoint? Output,
    AudioEndpoint? Input,
    bool SonarActive,
    bool MicBridgeRunning,
    HeadsetStatus? Headset,
    IReadOnlyList<string> Problems)
{
    /// <summary>Where each playback channel actually goes (differs from <see cref="Output"/> when a channel has its own device).</summary>
    public IReadOnlyDictionary<Channel, AudioEndpoint?> ChannelOutputs { get; init; } = new Dictionary<Channel, AudioEndpoint?>();
}

/// <summary>
/// Decides which physical output and microphone to use from the user's priority lists and live device state
/// (including whether the wireless headset is actually switched on), then applies it: routes every Sonar
/// channel to that output, bridges the mic into the virtual mic, keeps blocked devices disabled and keeps
/// Windows' defaults where they belong. Without Sonar it falls back to switching Windows' defaults directly.
/// </summary>
public sealed class Router : IDisposable
{
    static readonly Channel[] RenderChannels = [Channel.Game, Channel.Chat, Channel.Media, Channel.Aux];

    readonly AudioEndpoints _endpoints;
    readonly NovaProWireless? _headset;
    readonly SonarEngine? _sonar;
    readonly object _lock = new();
    readonly Timer _evaluateTimer;
    AppConfig _config;
    MicBridge? _micBridge;
    readonly Dictionary<Channel, string?> _appliedOutputs = new();
    bool _disposed;

    public RouteState State { get; private set; } = new(null, null, false, false, null, []);

    /// <summary>Echo cancelling on the mic needs to know what each channel plays on (as Sonar does).</summary>
    static ApoSetting AecDeviceSetting(Channel channel) => channel switch
    {
        Channel.Game => ApoSetting.AECGameDeviceId,
        Channel.Chat => ApoSetting.AECChatRenderDeviceId,
        Channel.Media => ApoSetting.AECMediaDeviceId,
        _ => ApoSetting.AECAuxDeviceId,
    };

    public event EventHandler<RouteState>? StateChanged;

    public Router(AudioEndpoints endpoints, NovaProWireless? headset, SonarEngine? sonar, AppConfig config)
    {
        _endpoints = endpoints;
        _headset = headset;
        _sonar = sonar;
        _config = config;
        _evaluateTimer = new Timer(_ => Evaluate());

        // Never evaluate on the notifying thread: Windows audio notifications must return immediately
        // and must not call back into the audio stack, so every trigger just schedules a pass.
        _endpoints.Changed += (_, _) => RequestEvaluate();
        _endpoints.DefaultChanged += (_, _) => RequestEvaluate(); // something else changed a default: re-assert ours
        if (_headset is not null)
        {
            _headset.StatusChanged += (_, _) => RequestEvaluate();
            _headset.ConnectedChanged += (_, _) => RequestEvaluate();
            _headset.ChatMixChanged += OnChatMix;
        }
    }

    public void UpdateConfig(AppConfig config)
    {
        lock (_lock)
            _config = config;
        RequestEvaluate();
    }

    /// <summary>Schedules an evaluation shortly on a timer thread (coalesces bursts of events).</summary>
    public void RequestEvaluate() => _evaluateTimer.Change(TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);

    /// <summary>Re-reads device state and applies the routing. Cheap and idempotent; called on every change.</summary>
    public void Evaluate()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var problems = new List<string>();
            var all = _endpoints.GetAll();
            var headset = _headset?.Status;

            EnforceBlocklist(all, problems);
            if (_sonar is not null && EnableSonarEndpoints(all, problems))
                all = _endpoints.GetAll();

            var output = FirstAvailable(_config.OutputPriority, all, EndpointFlow.Output, headset);
            var input = Pinned(Channel.Mic, all, EndpointFlow.Input, headset)
                ?? FirstAvailable(_config.InputPriority, all, EndpointFlow.Input, headset);
            if (output is null && _config.OutputPriority.Count > 0)
                problems.Add("None of the preferred outputs is available.");

            var channelOutputs = RenderChannels.ToDictionary(c => c, c => Pinned(c, all, EndpointFlow.Output, headset) ?? output);
            var sonarActive = _sonar is not null && SonarEndpoint(all, Channel.Game, EndpointFlow.Output) is not null;
            if (sonarActive)
            {
                ApplySonarRouting(channelOutputs, problems);
                ApplyMicBridge(input, all, problems);
            }
            if (_config.ManageWindowsDefaults)
                ApplyWindowsDefaults(all, output, input, sonarActive, problems);

            SetState(new RouteState(output, input, sonarActive, _micBridge is not null, headset, problems) { ChannelOutputs = channelOutputs });
        }
    }

    AudioEndpoint? FirstAvailable(List<DeviceRef> priority, IReadOnlyList<AudioEndpoint> all, EndpointFlow flow, HeadsetStatus? headset)
    {
        foreach (var wanted in priority)
        {
            var endpoint = all.FirstOrDefault(e => e.Id == wanted.Id && e.Flow == flow);
            if (endpoint is null || endpoint.State != EndpointState.Active || IsBlocked(endpoint))
                continue;
            // The base station stays plugged in when the headset is off; only use it while the headset is on.
            if (endpoint.IsNovaProWireless && headset is not null && !headset.IsHeadsetOn)
                continue;
            return endpoint;
        }
        return null;
    }

    /// <summary>The device a channel is pinned to, if it has one and it can be used right now.</summary>
    AudioEndpoint? Pinned(Channel channel, IReadOnlyList<AudioEndpoint> all, EndpointFlow flow, HeadsetStatus? headset) =>
        _config.Channels.TryGetValue(channel, out var c) && c.Device is { } device
            ? FirstAvailable([device], all, flow, headset)
            : null;

    bool IsBlocked(AudioEndpoint endpoint) => _config.Blocked.Any(b => b.Id == endpoint.Id);

    void EnforceBlocklist(IReadOnlyList<AudioEndpoint> all, List<string> problems)
    {
        foreach (var endpoint in all.Where(e => e.State == EndpointState.Active && IsBlocked(e)))
        {
            try
            {
                PolicyConfig.SetEnabled(endpoint.Id, false);
            }
            catch (Exception e)
            {
                problems.Add($"Couldn't disable blocked device {endpoint.DisplayName}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Turns Sonar's devices back on in Windows if something turned them off: GG disables them all when it's quit
    /// from its tray icon (killing it leaves them alone), which would leave nothing to route through. Media and Aux
    /// only while they're in the mixer, since removing them from the mixer turns them off on purpose.
    /// True if anything was turned on.
    /// </summary>
    bool EnableSonarEndpoints(IReadOnlyList<AudioEndpoint> all, List<string> problems)
    {
        var enabled = false;
        foreach (var endpoint in all.Where(e => e.State == EndpointState.Disabled && e.SonarChannel is { } c && !IsBlocked(e)
                                                && (c is Channel.Game or Channel.Chat or Channel.Mic || _config.MixerChannels.Contains(c))))
        {
            try
            {
                PolicyConfig.SetEnabled(endpoint.Id, true);
                enabled = true;
            }
            catch (Exception e)
            {
                problems.Add($"Couldn't turn on {endpoint.DisplayName}: {e.Message}");
            }
        }
        return enabled;
    }

    static AudioEndpoint? SonarEndpoint(IReadOnlyList<AudioEndpoint> all, Channel channel, EndpointFlow flow) =>
        all.FirstOrDefault(e => e.SonarChannel == channel && e.Flow == flow && e.State == EndpointState.Active);

    void ApplySonarRouting(Dictionary<Channel, AudioEndpoint?> outputs, List<string> problems)
    {
        foreach (var (channel, output) in outputs)
        {
            if (_appliedOutputs.TryGetValue(channel, out var applied) && applied == output?.Id)
                continue;
            try
            {
                _sonar!.SetOutputDevice(channel, output?.Id);
                _sonar.SetString(Channel.Mic, AecDeviceSetting(channel), output?.Id ?? "");
                _appliedOutputs[channel] = output?.Id;
            }
            catch (Exception e)
            {
                problems.Add($"Couldn't route {channel}: {e.Message}");
                _appliedOutputs.Remove(channel);
            }
        }
    }

    void ApplyMicBridge(AudioEndpoint? input, IReadOnlyList<AudioEndpoint> all, List<string> problems)
    {
        var target = SonarEndpoint(all, Channel.Mic, EndpointFlow.Output);
        if (_micBridge is not null && (_micBridge.SourceId != input?.Id || _micBridge.TargetId != target?.Id))
        {
            _micBridge.Dispose();
            _micBridge = null;
        }
        if (_micBridge is not null || input is null || target is null)
            return;
        try
        {
            var bridge = new MicBridge(_endpoints, input.Id, target.Id);
            bridge.Stopped += (_, _) => OnMicBridgeStopped(bridge);
            bridge.Start();
            _micBridge = bridge;
        }
        catch (Exception e)
        {
            problems.Add($"Couldn't start the microphone: {e.Message}");
            _evaluateTimer.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    void OnMicBridgeStopped(MicBridge bridge)
    {
        lock (_lock)
        {
            if (_micBridge != bridge)
                return;
            _micBridge.Dispose();
            _micBridge = null;
        }
        // Device glitch or format change: rebuild shortly.
        _evaluateTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    void ApplyWindowsDefaults(IReadOnlyList<AudioEndpoint> all, AudioEndpoint? output, AudioEndpoint? input, bool sonarActive, List<string> problems)
    {
        string? playback, communications, recording;
        if (sonarActive)
        {
            playback = SonarEndpoint(all, _config.DefaultPlaybackChannel, EndpointFlow.Output)?.Id;
            communications = SonarEndpoint(all, Channel.Chat, EndpointFlow.Output)?.Id;
            recording = SonarEndpoint(all, Channel.Mic, EndpointFlow.Input)?.Id;
        }
        else
        {
            playback = communications = output?.Id;
            recording = input?.Id;
        }
        TrySetDefault(EndpointFlow.Output, DeviceRole.Console, playback, problems);
        TrySetDefault(EndpointFlow.Output, DeviceRole.Multimedia, playback, problems);
        TrySetDefault(EndpointFlow.Output, DeviceRole.Communications, communications, problems);
        foreach (var role in Enum.GetValues<DeviceRole>())
            TrySetDefault(EndpointFlow.Input, role, recording, problems);
    }

    void TrySetDefault(EndpointFlow flow, DeviceRole role, string? deviceId, List<string> problems)
    {
        if (deviceId is null || _endpoints.GetDefaultId(flow, role) == deviceId)
            return;
        try
        {
            PolicyConfig.SetDefault(deviceId, role);
        }
        catch (Exception e)
        {
            problems.Add($"Couldn't set the default {flow.ToString().ToLowerInvariant()} device: {e.Message}");
        }
    }

    void OnChatMix(object? sender, (int Game, int Chat) dial)
    {
        if (!_config.ChatMixEnabled || _sonar is null)
            return;
        try
        {
            _sonar.SetChatMixBalance(SonarEngine.BalanceFromDial(dial.Game, dial.Chat));
        }
        catch (Exception)
        {
            // Transient; the next dial event retries.
        }
    }

    void SetState(RouteState state)
    {
        var changed = state.Output?.Id != State.Output?.Id || state.Input?.Id != State.Input?.Id
            || state.SonarActive != State.SonarActive || state.MicBridgeRunning != State.MicBridgeRunning
            || state.Headset != State.Headset || !state.Problems.SequenceEqual(State.Problems)
            || state.ChannelOutputs.Any(kv => !State.ChannelOutputs.TryGetValue(kv.Key, out var old) || old?.Id != kv.Value?.Id);
        State = state;
        if (changed)
            StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _evaluateTimer.Dispose();
            _micBridge?.Dispose();
            _micBridge = null;
            // Leave no channel attenuated once we stop managing ChatMix (Sonar never resets Media/Aux).
            try { _sonar?.SetChatMixBalance(0); }
            catch (Exception) { }
        }
    }
}
