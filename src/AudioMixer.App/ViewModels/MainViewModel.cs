using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using AudioMixer.App.Services;
using AudioMixer.Core.Audio;
using AudioMixer.Core.Config;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Routing;
using AudioMixer.Core.Sonar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioMixer.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    static readonly string[] EqBandLabels = ["32", "64", "125", "250", "500", "1k", "2k", "4k", "8k", "16k"];

    readonly AppHost _host;
    readonly Dispatcher _ui;
    readonly DispatcherTimer _saveDebounce;
    readonly DispatcherTimer _eqDebounce;
    bool _loadingHeadset;
    bool _lowBatteryNotified;

    /// <summary>Raised when the user should be told something (tray balloon).</summary>
    public event EventHandler<(string Title, string Message)>? Notify;

    public MainViewModel(AppHost host)
    {
        _host = host;
        _ui = Application.Current.Dispatcher;
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveConfig(); };
        _eqDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eqDebounce.Tick += (_, _) => { _eqDebounce.Stop(); ApplyEq(); };

        if (host.Sonar is { } sonar)
        {
            Channels.Add(new ChannelStripViewModel(sonar, Channel.Game, "Game"));
            Channels.Add(new ChannelStripViewModel(sonar, Channel.Chat, "Chat"));
            Channels.Add(new ChannelStripViewModel(sonar, Channel.Media, "Media"));
            Channels.Add(new ChannelStripViewModel(sonar, Channel.Aux, "Aux"));
        }
        for (var i = 0; i < 10; i++)
            EqBands.Add(new EqBandViewModel(EqBandLabels[i], OnEqBandChanged));

        LoadConfigIntoView(host.Config);
        RefreshDevices();
        UpdateHeadset(host.Headset.Status);
        UpdateRoute(host.Router?.State);
        UpdateActivation();

        host.Endpoints.Changed += (_, _) => _ui.BeginInvoke(RefreshDevices);
        host.Headset.StatusChanged += (_, s) => _ui.BeginInvoke(() => UpdateHeadset(s));
        host.Headset.ConnectedChanged += (_, c) => _ui.BeginInvoke(() =>
        {
            BaseStationConnected = c;
            if (c) _ = LoadHeadsetSettingsAsync();
            else UpdateHeadset(null);
        });
        host.Headset.ChatMixChanged += (_, d) => _ui.BeginInvoke(() => SetChatMixFromDial(d.Game, d.Chat));
        host.ActiveChanged += (_, _) => _ui.BeginInvoke(() =>
        {
            UpdateActivation();
            if (host.Router is { } router)
                router.StateChanged += (_, s) => _ui.BeginInvoke(() => UpdateRoute(s));
        });
        if (host.Router is { } router)
            router.StateChanged += (_, s) => _ui.BeginInvoke(() => UpdateRoute(s));

        BaseStationConnected = host.Headset.IsConnected;
        if (BaseStationConnected)
            _ = LoadHeadsetSettingsAsync();
    }

    // ===== Status =====

    [ObservableProperty] bool _isActive;
    [ObservableProperty] bool _ggIsRunning;
    [ObservableProperty] bool _sonarAvailable;
    [ObservableProperty] string _sonarStatus = "";
    [ObservableProperty] string _outputName = "—";
    [ObservableProperty] string _inputName = "—";
    [ObservableProperty] string _problems = "";
    [ObservableProperty] bool _baseStationConnected;
    [ObservableProperty] bool _headsetOn;
    [ObservableProperty] int _headsetBattery;
    [ObservableProperty] int _spareBattery;
    [ObservableProperty] string _chargeText = "";
    [ObservableProperty] bool _micMuted;
    [ObservableProperty] string _noiseControlText = "";

    public string HeadsetSummary => !BaseStationConnected ? "Base station not connected"
        : !HeadsetOn ? "Headset off"
        : $"Headset {HeadsetBattery}%  ·  spare {SpareBattery}%  ·  {ChargeText}";

    public string TrayTooltip => $"AudioMixer\n{HeadsetSummary}\nOutput: {OutputName}";

    partial void OnBaseStationConnectedChanged(bool value) => RaiseSummary();
    partial void OnHeadsetOnChanged(bool value) => RaiseSummary();
    partial void OnHeadsetBatteryChanged(int value) => RaiseSummary();
    partial void OnSpareBatteryChanged(int value) => RaiseSummary();
    partial void OnChargeTextChanged(string value) => RaiseSummary();
    partial void OnOutputNameChanged(string value) => OnPropertyChanged(nameof(TrayTooltip));

    void RaiseSummary()
    {
        OnPropertyChanged(nameof(HeadsetSummary));
        OnPropertyChanged(nameof(TrayTooltip));
    }

    void UpdateActivation()
    {
        IsActive = _host.IsActive;
        GgIsRunning = SteelSeriesGG.IsSonarRunning;
        SonarAvailable = _host.Sonar is not null;
        SonarStatus = _host.Sonar is not null ? "Sonar virtual channels available" : _host.SonarError ?? "Sonar not available";
        foreach (var c in Channels) c.Refresh();
    }

    void UpdateHeadset(HeadsetStatus? s)
    {
        HeadsetOn = s?.IsHeadsetOn == true;
        HeadsetBattery = s?.HeadsetBatteryPercent ?? 0;
        SpareBattery = s?.SpareBatteryPercent ?? 0;
        MicMuted = s?.MicMuted == true;
        ChargeText = s?.Charge switch
        {
            ChargeState.Charging => "charging",
            ChargeState.Charged => "charged",
            ChargeState.Discharging => "on battery",
            _ => "",
        };
        NoiseControlText = s?.NoiseControl switch
        {
            NoiseControl.Anc => "ANC",
            NoiseControl.Transparency => "Transparency",
            NoiseControl.Off => "Off",
            _ => "",
        };

        var threshold = _host.Config.Headset.LowBatteryWarningPercent;
        if (s is { IsHeadsetOn: true, Charge: ChargeState.Discharging } && threshold > 0 && s.HeadsetBatteryPercent <= threshold)
        {
            if (!_lowBatteryNotified)
            {
                _lowBatteryNotified = true;
                Notify?.Invoke(this, ("Headset battery low", $"{s.HeadsetBatteryPercent}% left — swap in the spare ({s.SpareBatteryPercent}%)."));
            }
        }
        else if (s is { IsHeadsetOn: true } && s.HeadsetBatteryPercent > threshold)
        {
            _lowBatteryNotified = false;
        }
    }

    void UpdateRoute(RouteState? s)
    {
        if (s is null)
            return;
        OutputName = s.Output?.Name ?? "None available";
        InputName = s.Input?.Name ?? "None available";
        Problems = string.Join("\n", s.Problems);
        foreach (var d in OutputDevices) d.IsCurrent = d.Id == s.Output?.Id;
        foreach (var d in InputDevices) d.IsCurrent = d.Id == s.Input?.Id;
    }

    [RelayCommand]
    void TakeOver()
    {
        _host.TakeOverFromGG(disableAutostart: true);
        UpdateActivation();
        Notify?.Invoke(this, ("AudioMixer is in control", "SteelSeries GG has been stopped and won't start with Windows. You can restore it in Settings."));
    }

    // ===== Mixer =====

    public ObservableCollection<ChannelStripViewModel> Channels { get; } = [];

    /// <summary>ChatMix position, −100 (all game/other) … +100 (all chat).</summary>
    [ObservableProperty] double _chatMix;
    bool _chatMixFromDial;

    void SetChatMixFromDial(int game, int chat)
    {
        _chatMixFromDial = true;
        ChatMix = Math.Round(SonarEngine.BalanceFromDial(game, chat) * 100);
        _chatMixFromDial = false;
    }

    partial void OnChatMixChanged(double value)
    {
        // Dial changes are already applied by the router; only push slider drags.
        if (!_chatMixFromDial && IsActive && _host.Sonar is { } sonar && _host.Config.ChatMixEnabled)
            sonar.SetChatMixBalance((float)(value / 100), _host.Config.ChatMixFadedChannels);
    }

    [ObservableProperty] bool _chatMixFadesGame;
    [ObservableProperty] bool _chatMixFadesMedia;
    [ObservableProperty] bool _chatMixFadesAux;
    [ObservableProperty] bool _chatMixEnabled;

    partial void OnChatMixFadesGameChanged(bool value) => QueueSave();
    partial void OnChatMixFadesMediaChanged(bool value) => QueueSave();
    partial void OnChatMixFadesAuxChanged(bool value) => QueueSave();
    partial void OnChatMixEnabledChanged(bool value) => QueueSave();

    // ===== Devices =====

    public ObservableCollection<DeviceItemViewModel> OutputDevices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> InputDevices { get; } = [];

    void RefreshDevices()
    {
        var all = _host.Endpoints.GetAll().Where(e => !e.IsVirtual).ToList();
        Sync(OutputDevices, _host.Config.OutputPriority, all.Where(e => e.Flow == EndpointFlow.Output).ToList(), EndpointFlow.Output);
        Sync(InputDevices, _host.Config.InputPriority, all.Where(e => e.Flow == EndpointFlow.Input).ToList(), EndpointFlow.Input);
        UpdateRoute(_host.Router?.State);
    }

    /// <summary>Endpoint ids encode their direction: render ids start {0.0.0…}, capture ids {0.0.1…}.</summary>
    static bool IsFlow(string id, EndpointFlow flow) =>
        id.StartsWith(flow == EndpointFlow.Output ? "{0.0.0." : "{0.0.1.", StringComparison.Ordinal);

    /// <summary>
    /// Lists configured devices in priority order, then blocked ones (shown even while absent so they can be
    /// unblocked), then any other devices on this PC (unticked).
    /// </summary>
    void Sync(ObservableCollection<DeviceItemViewModel> items, List<DeviceRef> priority, List<AudioEndpoint> present, EndpointFlow flow)
    {
        if (items.Count == 0)
        {
            foreach (var d in priority)
                items.Add(Track(new DeviceItemViewModel(d.Id, d.Name) { Enabled = true }));
            foreach (var b in _host.Config.Blocked.Where(b => priority.All(p => p.Id != b.Id) && IsFlow(b.Id, flow)))
                items.Add(Track(new DeviceItemViewModel(b.Id, b.Name)));
        }
        foreach (var e in present.Where(e => items.All(i => i.Id != e.Id)))
            items.Add(Track(new DeviceItemViewModel(e.Id, e.DisplayName)));
        foreach (var item in items)
        {
            var endpoint = present.FirstOrDefault(e => e.Id == item.Id);
            item.State = endpoint?.State;
            if (endpoint is not null)
                item.Name = endpoint.DisplayName;
            item.Blocked = _host.Config.Blocked.Any(b => b.Id == item.Id);
        }
    }

    DeviceItemViewModel Track(DeviceItemViewModel item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceItemViewModel.Enabled))
                QueueSave();
            else if (e.PropertyName == nameof(DeviceItemViewModel.Blocked))
                OnBlockedToggled(item);
        };
        return item;
    }

    void OnBlockedToggled(DeviceItemViewModel item)
    {
        var wasBlocked = _host.Config.Blocked.Any(b => b.Id == item.Id);
        if (wasBlocked == item.Blocked)
            return;
        if (item.Blocked)
            item.Enabled = false;
        else
        {
            // Unblocking: give the device back to Windows.
            try { PolicyConfig.SetEnabled(item.Id, true); } catch (Exception) { }
        }
        SaveConfig();
    }

    [RelayCommand]
    void MoveUp(DeviceItemViewModel item) => Move(item, -1);

    [RelayCommand]
    void MoveDown(DeviceItemViewModel item) => Move(item, +1);

    void Move(DeviceItemViewModel item, int delta)
    {
        var list = OutputDevices.Contains(item) ? OutputDevices : InputDevices;
        var index = list.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
            return;
        list.Move(index, target);
        QueueSave();
    }

    // ===== Headset =====

    public ObservableCollection<EqBandViewModel> EqBands { get; } = [];
    [ObservableProperty] bool _eqCustom;
    [ObservableProperty] int _sidetone;
    [ObservableProperty] int _micVolume = 10;
    [ObservableProperty] int _lowBatteryWarning;

    async Task LoadHeadsetSettingsAsync()
    {
        try
        {
            var a = await _host.Headset.GetAudioSettingsAsync();
            _loadingHeadset = true;
            for (var i = 0; i < 10; i++)
                EqBands[i].Gain = a.CustomEqDb[i];
            EqCustom = a.EqPreset == 4;
            Sidetone = a.Sidetone;
            MicVolume = Math.Max(1, a.MicVolume);
        }
        catch (Exception)
        {
        }
        finally
        {
            _loadingHeadset = false;
        }
    }

    void OnEqBandChanged()
    {
        if (_loadingHeadset)
            return;
        EqCustom = true;
        _eqDebounce.Stop();
        _eqDebounce.Start();
    }

    void ApplyEq()
    {
        try { _host.Headset.SetCustomEq(EqBands.Select(b => b.Gain).ToList()); }
        catch (Exception e) { Problems = $"Couldn't set headset EQ: {e.Message}"; }
    }

    [RelayCommand]
    void ResetEq()
    {
        foreach (var band in EqBands)
            band.Gain = 0;
    }

    partial void OnSidetoneChanged(int value)
    {
        if (!_loadingHeadset) Try(() => _host.Headset.SetSidetone(value));
    }

    partial void OnMicVolumeChanged(int value)
    {
        if (!_loadingHeadset) Try(() => _host.Headset.SetMicVolume(value));
    }

    partial void OnLowBatteryWarningChanged(int value) => QueueSave();

    void Try(Action action)
    {
        try { action(); }
        catch (Exception e) { Problems = e.Message; }
    }

    // ===== Settings =====

    [ObservableProperty] bool _startWithWindows;
    [ObservableProperty] bool _manageWindowsDefaults;
    public bool GgAutostartEnabled => SteelSeriesGG.IsAutostartEnabled;

    partial void OnStartWithWindowsChanged(bool value) => Try(() => Autostart.Set(value));
    partial void OnManageWindowsDefaultsChanged(bool value) => QueueSave();

    [RelayCommand]
    void RestoreGG()
    {
        SteelSeriesGG.RestoreAutostart();
        OnPropertyChanged(nameof(GgAutostartEnabled));
        Notify?.Invoke(this, ("SteelSeries GG restored", "GG will start with Windows again. Quit AudioMixer before starting GG so they don't fight over your audio."));
    }

    // ===== Config =====

    void LoadConfigIntoView(AppConfig c)
    {
        _chatMixEnabled = c.ChatMixEnabled;
        _chatMixFadesGame = c.ChatMixFadedChannels.Contains(Channel.Game);
        _chatMixFadesMedia = c.ChatMixFadedChannels.Contains(Channel.Media);
        _chatMixFadesAux = c.ChatMixFadedChannels.Contains(Channel.Aux);
        _manageWindowsDefaults = c.ManageWindowsDefaults;
        _lowBatteryWarning = c.Headset.LowBatteryWarningPercent;
        _startWithWindows = Autostart.IsEnabled;
    }

    void QueueSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    void SaveConfig()
    {
        var old = _host.Config;
        var faded = new List<Channel>();
        if (ChatMixFadesGame) faded.Add(Channel.Game);
        if (ChatMixFadesMedia) faded.Add(Channel.Media);
        if (ChatMixFadesAux) faded.Add(Channel.Aux);
        var config = new AppConfig
        {
            OutputPriority = OutputDevices.Where(d => d.Enabled && !d.Blocked).Select(d => new DeviceRef(d.Id, d.Name)).ToList(),
            InputPriority = InputDevices.Where(d => d.Enabled && !d.Blocked).Select(d => new DeviceRef(d.Id, d.Name)).ToList(),
            Blocked = OutputDevices.Concat(InputDevices).Where(d => d.Blocked).Select(d => new DeviceRef(d.Id, d.Name))
                .Concat(old.Blocked.Where(b => OutputDevices.Concat(InputDevices).All(d => d.Id != b.Id)))
                .ToList(),
            ManageWindowsDefaults = ManageWindowsDefaults,
            DefaultPlaybackChannel = old.DefaultPlaybackChannel,
            ChatMixEnabled = ChatMixEnabled,
            ChatMixFadedChannels = faded,
            Headset = new HeadsetConfig { LowBatteryWarningPercent = LowBatteryWarning },
        };
        _host.SaveConfig(config);
        if (_host.Sonar is { } sonar && IsActive)
            Try(() => sonar.SetChatMixBalance((float)(ChatMix / 100), config.ChatMixEnabled ? faded : []));
    }
}

public sealed partial class EqBandViewModel(string label, Action changed) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Gain in dB, −10…+10 in 0.5 dB steps (the base station's resolution).</summary>
    [ObservableProperty] double _gain;

    partial void OnGainChanged(double value)
    {
        var snapped = Math.Round(value * 2) / 2;
        if (snapped != value)
        {
            Gain = snapped;
            return;
        }
        changed();
    }
}
