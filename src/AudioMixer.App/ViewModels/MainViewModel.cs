using System.IO;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
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
    readonly ChannelMonitor _monitor = new();
    readonly DispatcherTimer _saveDebounce;
    readonly DispatcherTimer _eqDebounce;
    readonly DispatcherTimer _flashDebounce;
    readonly DispatcherTimer _meterTimer;
    readonly DispatcherTimer _appsTimer;
    readonly DispatcherTimer _headsetReload;
    readonly DispatcherTimer _soundSave;
    readonly DispatcherTimer _toastTimer;
    readonly PresetStore _presets = new();
    readonly OledWidget _oled;
    readonly List<ChannelStripViewModel> _allStrips = [];
    bool _loadingHeadset;
    bool _lowBatteryNotified;

    /// <summary>"Version 0.1.0", from the build (src/Directory.Build.props).</summary>
    public static string AppVersion { get; } = "Version " + (System.Reflection.Assembly.GetEntryAssembly()
        ?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "?");

    /// <summary>Raised when the user should be told something (tray balloon).</summary>
    public event EventHandler<(string Title, string Message)>? Notify;

    public MainViewModel(AppHost host)
    {
        _host = host;
        _ui = Application.Current.Dispatcher;
        _saveDebounce = Debounce(400, SaveConfig);
        _eqDebounce = Debounce(150, ApplyEq);
        // Settings changed on the base station are written to its flash once things go quiet.
        // Base station changes are read back once things go quiet; if they took, they're stored in its flash.
        _flashDebounce = Debounce(1200, () => _ = VerifyAndSaveHeadsetAsync());
        _toastTimer = Debounce(2800, () => ToastVisible = false);
        _oled = new OledWidget(host.Headset);
        _oled.Rendered += (_, image) => OledPreview = image;
        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += (_, _) => UpdateMeters();
        _appsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _appsTimer.Tick += (_, _) => UpdateApps();
        // Settings changed on the base station's own menu arrive one push at a time; re-read once they settle.
        _headsetReload = Debounce(400, () => _ = LoadHeadsetSettingsAsync());
        // EQ edits only need writing to disk; the router doesn't care about them.
        _soundSave = Debounce(800, () => Try(() => _host.Config.Save()));

        if (host.Sonar is { } sonar)
        {
            _allStrips.Add(ChannelStripViewModel.ForSonar(sonar, Channel.Game, "Game"));
            _allStrips.Add(ChannelStripViewModel.ForSonar(sonar, Channel.Chat, "Chat"));
            _allStrips.Add(ChannelStripViewModel.ForSonar(sonar, Channel.Media, "Media"));
            _allStrips.Add(ChannelStripViewModel.ForSonar(sonar, Channel.Aux, "Aux"));
        }
        MicStrip = ChannelStripViewModel.ForMic(_monitor);
        MicStrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChannelStripViewModel.Muted))
            {
                UpdateOled();
                OnPropertyChanged(nameof(MicStatusText));
                OnPropertyChanged(nameof(IsMicSilenced));
            }
        };
        _allStrips.Add(MicStrip);
        if (host.Sonar is { } engine)
            CreateSounds(engine);
        for (var i = 0; i < 10; i++)
            EqBands.Add(new EqBandViewModel(EqBandLabels[i], OnEqBandChanged));

        LoadConfigIntoView(host.Config);
        RefreshDevices();
        UpdateHeadset(host.Headset.Status);
        UpdateRoute(host.Router?.State);
        UpdateActivation();

        host.Endpoints.Changed += (_, _) => _ui.BeginInvoke(() =>
        {
            RefreshDevices();
            _monitor.Refresh();
        });
        host.Headset.StatusChanged += (_, s) => _ui.BeginInvoke(() => UpdateHeadset(s));
        host.Headset.ConnectedChanged += (_, c) => _ui.BeginInvoke(() =>
        {
            BaseStationConnected = c;
            if (c) _ = LoadHeadsetSettingsAsync();
            else UpdateHeadset(null);
        });
        host.Headset.ChatMixChanged += (_, d) => _ui.BeginInvoke(() =>
        {
            _oled.NotifyActivity();
            SetChatMixFromDial(d.Game, d.Chat);
        });
        host.Headset.VolumeChanged += (_, v) => _ui.BeginInvoke(() =>
        {
            _oled.NotifyActivity();
            HeadsetVolume = v;
        });
        host.Headset.SettingsChanged += (_, _) => _ui.BeginInvoke(() => Restart(_headsetReload));
        host.ActiveChanged += (_, _) => _ui.BeginInvoke(() =>
        {
            UpdateActivation();
            ApplyAllSounds();
            if (host.Router is { } router)
                router.StateChanged += (_, s) => _ui.BeginInvoke(() => UpdateRoute(s));
        });
        if (host.Router is { } router)
            router.StateChanged += (_, s) => _ui.BeginInvoke(() => UpdateRoute(s));

        BaseStationConnected = host.Headset.IsConnected;
        if (BaseStationConnected)
            _ = LoadHeadsetSettingsAsync();
        if (host.IsActive)
            ApplyAllSounds();
        InitScreen(host.Config.Headset);
    }

    // ===== Sound (EQ and effects) =====

    /// <summary>One per channel, in mixer order; the EQ page edits <see cref="SelectedSound"/>.</summary>
    public ObservableCollection<ChannelSoundViewModel> Sounds { get; } = [];
    [ObservableProperty] ChannelSoundViewModel? _selectedSound;
    /// <summary>The page shown: 0 Mixer, 1 EQ, 2 Devices, 3 Headset, 4 Settings.</summary>
    [ObservableProperty] int _selectedTab;

    void CreateSounds(SonarEngine sonar)
    {
        var sonarPresets = SonarPresets.Load();
        var firstRun = false;
        foreach (var strip in _allStrips)
        {
            var config = _host.Config.ForChannel(strip.Channel);
            if (config.Sound is null)
            {
                // First run: carry over exactly what Sonar had set, under the name Sonar showed.
                firstRun = true;
                try { config.Sound = SoundApo.FromApo(sonar, strip.Channel); }
                catch (Exception) { config.Sound = SoundSettings.Flat(strip.Channel); }
                var selected = sonarPresets.FirstOrDefault(p => p.Channel == strip.Channel && p.IsSelectedInSonar);
                config.PresetName = selected?.Name ?? "Custom";
                config.Modified = false;
            }
            var color = new SolidColorBrush(ChannelStripViewModel.ColorOf(strip.Channel));
            color.Freeze();
            var sound = new ChannelSoundViewModel(strip.Channel, strip.Name, color, config, _presets, sonarPresets,
                (channel, settings) => Try(() => sonar.ApplySound(channel, settings)), () => Restart(_soundSave));
            sound.Notice += (_, text) => Toast(text);
            strip.Sound = sound;
            Sounds.Add(sound);
        }
        SelectedSound = Sounds.FirstOrDefault();
        if (firstRun)
            Try(() => _host.Config.Save());
    }

    /// <summary>Makes the engine match every channel's saved sound (when we take over from GG, or start in control).</summary>
    void ApplyAllSounds()
    {
        if (!_host.IsActive || _host.Sonar is not { } sonar)
            return;
        foreach (var sound in Sounds)
            Try(() => sonar.ApplySound(sound.Channel, _host.Config.ForChannel(sound.Channel).Sound!, force: true));
    }

    [RelayCommand]
    void OpenHeadset() => SelectedTab = 3;

    [RelayCommand]
    void OpenSound(ChannelStripViewModel? strip)
    {
        if (strip?.Sound is null)
            return;
        SelectedSound = strip.Sound;
        SelectedTab = 1;
    }

    DispatcherTimer Debounce(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        return timer;
    }

    static void Restart(DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    /// <summary>Set by the window: live meters and app lists only update while it is on screen.</summary>
    public bool IsViewVisible
    {
        set
        {
            if (value)
            {
                foreach (var strip in _allStrips) strip.Refresh();
                UpdateApps();
                _meterTimer.Start();
                _appsTimer.Start();
            }
            else
            {
                _meterTimer.Stop();
                _appsTimer.Stop();
            }
        }
    }

    // ===== Toast: a short "done" message after each change =====

    [ObservableProperty] string _toastText = "";
    [ObservableProperty] bool _toastIsError;
    [ObservableProperty] bool _toastVisible;

    public void Toast(string text, bool error = false)
    {
        ToastText = text;
        ToastIsError = error;
        ToastVisible = true;
        Restart(_toastTimer);
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
    [ObservableProperty] string _bluetoothText = "";
    [ObservableProperty] int _headsetVolume;

    public string HeadsetSummary => !BaseStationConnected ? "Base station not connected"
        : !HeadsetOn ? "Headset off"
        : $"Headset {HeadsetBattery}%  ·  spare {SpareBattery}%  ·  {ChargeText}";

    /// <summary>One plain line about the headset for the mixer and the sidebar.</summary>
    public string HeadsetStateText => !BaseStationConnected ? "Base station not found. Is it plugged in?"
        : !HeadsetOn ? "Headset is off"
        : $"Connected · {ChargeText}";

    /// <summary>Short status for the title bar.</summary>
    public string HeaderStatus => !BaseStationConnected ? "BASE STATION NOT FOUND"
        : !HeadsetOn ? "HEADSET OFF"
        : $"HEADSET ON  ·  {HeadsetBattery}%";

    public string TrayTooltip => $"AudioMixer\n{HeadsetSummary}\nOutput: {OutputName}";

    /// <summary>The mic's state across the headset's own mute switch and our software mute.</summary>
    public string MicStatusText => MicMuted ? "Mic muted on headset" : MicStrip.Muted ? "Mic muted" : "Mic live";

    /// <summary>True when nothing from the mic reaches apps, whichever mute did it.</summary>
    public bool IsMicSilenced => MicMuted || MicStrip.Muted;

    partial void OnBaseStationConnectedChanged(bool value) => RaiseSummary();
    partial void OnHeadsetOnChanged(bool value) => RaiseSummary();
    partial void OnHeadsetBatteryChanged(int value) => RaiseSummary();
    partial void OnSpareBatteryChanged(int value) => RaiseSummary();
    partial void OnChargeTextChanged(string value) => RaiseSummary();
    partial void OnMicMutedChanged(bool value)
    {
        UpdateOled();
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(IsMicSilenced));
    }
    partial void OnOutputNameChanged(string value) => OnPropertyChanged(nameof(TrayTooltip));

    void RaiseSummary()
    {
        UpdateOled();
        OnPropertyChanged(nameof(HeadsetSummary));
        OnPropertyChanged(nameof(HeaderStatus));
        OnPropertyChanged(nameof(HeadsetStateText));
        OnPropertyChanged(nameof(TrayTooltip));
        UpdateStatusRows();
    }

    void UpdateActivation()
    {
        IsActive = _host.IsActive;
        GgIsRunning = SteelSeriesGG.IsSonarRunning;
        SonarAvailable = _host.Sonar is not null;
        SonarStatus = _host.Sonar is not null ? "Sonar audio engine available" : _host.SonarError ?? "Sonar not available";
        if (!IsActive)
        {
            OutputName = InputName = "set by SteelSeries GG";
            _autoOutput.Label = _autoInput.Label = "Automatic";
            foreach (var strip in _allStrips)
                strip.DeviceName = "Set by SteelSeries GG";
        }
        foreach (var strip in _allStrips) strip.Refresh();
    }

    HeadsetStatus? _status;

    void UpdateHeadset(HeadsetStatus? s)
    {
        _status = s;
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
            NoiseControl.Anc => "Noise cancelling",
            NoiseControl.Transparency => "Transparency",
            NoiseControl.Off => "Noise control off",
            _ => "—",
        };
        BluetoothText = s is null ? "—" : s.BluetoothOn ? "Bluetooth on" : "Bluetooth off";

        if (s is not null)
        {
            _loadingHeadset = true;
            RangeMode = s.RangeMode;
            BluetoothOnAtStartup = s.BluetoothOnAtStartup;
            CallMode = s.BluetoothCallMode;
            MutedMicBrightness = Math.Clamp(s.MutedMicBrightness, 1, 10);
            SleepAfterMinutes = s.SleepAfterMinutes;
            _loadingHeadset = false;
        }
        UpdateStatusRows();

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
        _autoOutput.Label = $"Auto · {OutputName}";
        _autoInput.Label = $"Auto · {InputName}";
        foreach (var strip in _allStrips)
            strip.DeviceName = strip.IsMic ? InputName
                : s.ChannelOutputs.TryGetValue(strip.Channel, out var o) ? o?.Name ?? "None available" : OutputName;
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

    /// <summary>The strips shown in the mixer (see <see cref="ChannelToggles"/>).</summary>
    public ObservableCollection<ChannelStripViewModel> Channels { get; } = [];
    public ObservableCollection<ChannelToggleViewModel> ChannelToggles { get; } = [];
    public ChannelStripViewModel MicStrip { get; }

    /// <summary>Channels you can add to the mixer (turned off in Windows while they're not in it).</summary>
    public ObservableCollection<ChannelStripViewModel> HiddenChannels { get; } = [];
    public bool CanAddChannel => HiddenChannels.Count > 0;

    /// <summary>Media and Aux are optional; Game, Chat and Mic are what Windows' default devices point at.</summary>
    public static bool IsRemovable(Channel channel) => channel is Channel.Media or Channel.Aux;

    [RelayCommand]
    void AddChannel(ChannelStripViewModel? strip) => SetChannelShown(strip, true);

    [RelayCommand]
    void RemoveChannel(ChannelStripViewModel? strip) => SetChannelShown(strip, false);

    void SetChannelShown(ChannelStripViewModel? strip, bool shown)
    {
        var toggle = ChannelToggles.FirstOrDefault(t => t.Strip == strip);
        if (toggle is null || toggle.IsShown == shown)
            return;
        toggle.IsShown = shown; // rebuilds and saves
        var endpoint = _host.Endpoints.GetAll().FirstOrDefault(e => e.SonarChannel == toggle.Channel && e.Flow == EndpointFlow.Output);
        try
        {
            if (endpoint is not null)
                PolicyConfig.SetEnabled(endpoint.Id, shown);
            Toast(shown ? $"{toggle.Name} added: apps can now pick “{endpoint?.Name ?? toggle.Name}”"
                        : $"{toggle.Name} removed and turned off in Windows");
        }
        catch (Exception e)
        {
            Toast($"Couldn't switch {toggle.Name} in Windows: {e.Message}", error: true);
        }
    }

    void RebuildChannels()
    {
        HiddenChannels.Clear();
        foreach (var toggle in ChannelToggles.Where(t => !t.IsShown && IsRemovable(t.Channel)))
            HiddenChannels.Add(toggle.Strip);
        OnPropertyChanged(nameof(CanAddChannel));
        Channels.Clear();
        foreach (var toggle in ChannelToggles.Where(t => t.IsShown))
            Channels.Add(toggle.Strip);
    }

    void UpdateMeters()
    {
        foreach (var strip in Channels)
        {
            // Peak meters read in dB feel right; map −60…0 dBFS onto the bar.
            var peak = _monitor.GetPeak(strip.Channel);
            var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
            var target = Math.Clamp((db + 60) / 60 * 100, 0, 100);
            // Instant rise, gentle fall.
            strip.Peak = target >= strip.Peak ? target : Math.Max(target, strip.Peak - 3);
        }
    }

    void UpdateApps()
    {
        foreach (var strip in Channels)
        {
            var apps = _monitor.GetApps(strip.Channel);
            strip.Apps = strip.IsMic ? "Apps record this as “Sonar - Microphone”" : apps.Count == 0 ? "No apps playing" : string.Join(", ", apps);
        }
    }

    /// <summary>ChatMix position, −100 (all game) … +100 (all chat).</summary>
    [ObservableProperty] double _chatMix;
    bool _chatMixFromDial;

    // With ChatMix off both stay at full volume, whatever the dial says.
    public int ChatMixGamePercent => ChatMixEnabled ? (int)Math.Round(SonarEngine.ChatMixGains((float)(ChatMix / 100)).Game * 100) : 100;
    public int ChatMixChatPercent => ChatMixEnabled ? (int)Math.Round(SonarEngine.ChatMixGains((float)(ChatMix / 100)).Chat * 100) : 100;

    /// <summary>Picks up where the dial is (it may have moved while ChatMix was off).</summary>
    async Task RefreshChatMixFromBaseAsync()
    {
        try
        {
            var a = await _host.Headset.GetAudioSettingsAsync();
            SetChatMixFromDial(a.ChatMixGame, a.ChatMixChat);
            if (_host.Sonar is { } sonar && ChatMixEnabled)
                sonar.SetChatMixBalance((float)(ChatMix / 100));
        }
        catch (Exception) { }
    }

    void SetChatMixFromDial(int game, int chat)
    {
        _chatMixFromDial = true;
        ChatMix = Math.Round(SonarEngine.BalanceFromDial(game, chat) * 100);
        _chatMixFromDial = false;
    }

    partial void OnChatMixChanged(double value)
    {
        OnPropertyChanged(nameof(ChatMixGamePercent));
        OnPropertyChanged(nameof(ChatMixChatPercent));
        // The bar only shows the dial: the base station has no command to move its own ChatMix,
        // so letting the app change it would leave the two out of step.
    }

    [ObservableProperty] bool _chatMixEnabled;

    partial void OnChatMixEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ChatMixGamePercent));
        OnPropertyChanged(nameof(ChatMixChatPercent));
        if (IsActive)
        {
            // Same switch as Sonar's: turns ChatMix on the base station's dial on or off.
            Try(() => _host.Headset.SetSoftwareChatMix(value));
            Toast(value ? "ChatMix on: the base station dial balances Game and Chat" : "ChatMix off: Game and Chat both at full volume");
            if (value)
                _ = RefreshChatMixFromBaseAsync();
        }
        QueueSave();
    }

    [RelayCommand]
    void ToggleMicMute() => MicStrip.Muted = !MicStrip.Muted;

    // ===== Devices =====

    public ObservableCollection<DeviceItemViewModel> OutputDevices { get; } = [];
    public ObservableCollection<DeviceItemViewModel> InputDevices { get; } = [];

    void RefreshDevices()
    {
        var all = _host.Endpoints.GetAll().Where(e => !e.IsVirtual).ToList();
        Sync(OutputDevices, _host.Config.OutputPriority, all.Where(e => e.Flow == EndpointFlow.Output).ToList(), EndpointFlow.Output);
        Sync(InputDevices, _host.Config.InputPriority, all.Where(e => e.Flow == EndpointFlow.Input).ToList(), EndpointFlow.Input);
        RefreshDeviceChoices(all);
        UpdateRoute(_host.Router?.State);
    }

    readonly DeviceChoice _autoOutput = new(null, "Automatic");
    readonly DeviceChoice _autoInput = new(null, "Automatic");

    /// <summary>Rebuilds each strip's device picker: Automatic, then the usable devices (plus the pinned one if it's away).</summary>
    void RefreshDeviceChoices(List<AudioEndpoint> all)
    {
        List<DeviceChoice> Build(EndpointFlow flow, DeviceChoice auto, DeviceRef? pinned)
        {
            var list = new List<DeviceChoice> { auto };
            list.AddRange(all.Where(e => e.Flow == flow && e.State == EndpointState.Active && !_host.Config.Blocked.Any(b => b.Id == e.Id))
                .OrderBy(e => e.DisplayName).Select(e => new DeviceChoice(e.Id, e.DisplayName)));
            if (pinned is not null && list.All(c => c.Id != pinned.Id))
                list.Add(new DeviceChoice(pinned.Id, $"{pinned.Name} (not connected)"));
            return list;
        }

        foreach (var strip in _allStrips)
        {
            var pinned = _host.Config.ForChannel(strip.Channel).Device;
            strip.DeviceChoices = strip.IsMic ? Build(EndpointFlow.Input, _autoInput, pinned) : Build(EndpointFlow.Output, _autoOutput, pinned);
            strip.ShowDevice(strip.DeviceChoices.FirstOrDefault(c => c.Id == pinned?.Id) ?? strip.DeviceChoices[0]);
            strip.DeviceChosen -= OnStripDeviceChosen;
            strip.DeviceChosen += OnStripDeviceChosen;
        }
    }

    void OnStripDeviceChosen(object? sender, EventArgs e)
    {
        if (sender is not ChannelStripViewModel { SelectedDevice: { } choice } strip)
            return;
        _host.Config.ForChannel(strip.Channel).Device = choice.Id is null ? null
            : new DeviceRef(choice.Id, choice.Label.Replace(" (not connected)", ""));
        SaveConfig();
        var name = choice.Id is null ? "the first available device" : choice.Label;
        Toast(strip.IsMic ? $"Mic now uses {name}" : $"{strip.Name} now plays on {name}");
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
        Renumber(items);
    }

    static void Renumber(ObservableCollection<DeviceItemViewModel> items)
    {
        var rank = 1;
        foreach (var item in items)
            item.Rank = item.Enabled && !item.Blocked ? rank++ : 0;
    }

    DeviceItemViewModel Track(DeviceItemViewModel item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceItemViewModel.Enabled))
            {
                Renumber(OutputDevices.Contains(item) ? OutputDevices : InputDevices);
                QueueSave();
            }
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
        Renumber(OutputDevices.Contains(item) ? OutputDevices : InputDevices);
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
        Renumber(list);
        QueueSave();
    }

    // ===== Headset =====

    public static IReadOnlyList<Option> EqPresetOptions { get; } =
        [new(0, "Flat"), new(1, "Bass boost"), new(2, "Focus"), new(3, "Smiley"), new(4, "Custom")];
    public static IReadOnlyList<Option> SidetoneOptions { get; } = [new(0, "Off"), new(1, "Low"), new(2, "Medium"), new(3, "High")];
    public static IReadOnlyList<Option> TimerOptions { get; } =
        NovaProProtocol.TimerMinutes.Select(m => new Option(m, m == 0 ? "Never" : $"{m} min")).ToList();
    public static IReadOnlyList<Option> CallModeOptions { get; } =
        [new(BluetoothCallMode.DoNothing, "Nothing"), new(BluetoothCallMode.LowerOtherAudio, "Lower other audio"), new(BluetoothCallMode.MuteOtherAudio, "Mute other audio")];
    public static IReadOnlyList<Option> LineOutOptions { get; } = [new(LineOutMode.ChatMix, "ChatMix"), new(LineOutMode.StreamMix, "Stream mix")];
    public static IReadOnlyList<Option> HomeScreenOptions { get; } = [new(false, "Detailed"), new(true, "Simple")];
    public static IReadOnlyList<Option> WirelessOptions { get; } = [new(false, "Speed"), new(true, "Range")];

    public ObservableCollection<EqBandViewModel> EqBands { get; } = [];
    [ObservableProperty] int _eqPreset;
    [ObservableProperty] bool _highGain;
    [ObservableProperty] int _sidetone;
    [ObservableProperty] int _micVolume = 10;
    [ObservableProperty] LineOutMode _lineOut = LineOutMode.ChatMix;
    [ObservableProperty] int _brightness = 10;
    [ObservableProperty] int _dimAfterMinutes;
    [ObservableProperty] bool _simpleHomeScreen;
    [ObservableProperty] bool _bluetoothOnAtStartup;
    [ObservableProperty] BluetoothCallMode _callMode;
    [ObservableProperty] int _mutedMicBrightness = 10;
    [ObservableProperty] int _sleepAfterMinutes;
    [ObservableProperty] bool _rangeMode;
    [ObservableProperty] string _baseInput = "—";
    [ObservableProperty] int _streamMain = 100;
    [ObservableProperty] int _streamAux = 100;
    [ObservableProperty] int _streamMic = 100;
    [ObservableProperty] string _firmwareText = "";
    public bool IsStreamMix => LineOut == LineOutMode.StreamMix;
    [ObservableProperty] int _lowBatteryWarning;
    [ObservableProperty] IReadOnlyList<StatusRow> _statusRows = [];

    async Task LoadHeadsetSettingsAsync()
    {
        try
        {
            var a = await _host.Headset.GetAudioSettingsAsync();
            var ux = await _host.Headset.GetUxSettingsAsync();
            var firmware = await _host.Headset.GetFirmwareAsync();
            _loadingHeadset = true;
            for (var i = 0; i < 10; i++)
                EqBands[i].Gain = a.CustomEqDb[i];
            EqPreset = a.EqPreset;
            HighGain = a.HighGain;
            Sidetone = a.Sidetone;
            MicVolume = Math.Max(1, a.MicVolume);
            LineOut = a.LineOut;
            HeadsetVolume = a.Volume;
            BaseInput = a.InputName;
            SetChatMixFromDial(a.ChatMixGame, a.ChatMixChat);
            Brightness = Math.Clamp(ux.Brightness, 1, 10);
            DimAfterMinutes = ux.DimAfterMinutes;
            SimpleHomeScreen = ux.SimpleHomeScreen;
            StreamMain = a.StreamMain;
            StreamAux = a.StreamAux;
            StreamMic = a.StreamMic;
            FirmwareText = string.IsNullOrEmpty(firmware.HeadsetMcu)
                ? $"base {firmware.BaseMcu}"
                : $"base {firmware.BaseMcu} · headset {firmware.HeadsetMcu}";
        }
        catch (Exception)
        {
        }
        finally
        {
            _loadingHeadset = false;
            UpdateStatusRows();
        }
    }

    void UpdateStatusRows()
    {
        var s = _status;
        if (!BaseStationConnected || s is null)
        {
            StatusRows = [new("Base station", "Not connected")];
            return;
        }
        StatusRows =
        [
            new("Headset", HeadsetOn ? $"On, {ChargeText}" : "Off", HeadsetOn),
            new("Headset battery", $"{HeadsetBattery}%"),
            new("Spare battery", $"{SpareBattery}%"),
            new("2.4 GHz link", HeadsetOn ? "Connected" : "Not connected", HeadsetOn),
            new("Bluetooth", s.BluetoothOn ? "On" : "Off"),
            new("Mic", MicMuted ? "Muted on headset" : MicStrip.Muted ? "Muted in AudioMixer" : "Live", !IsMicSilenced),
            new("Noise control", s.NoiseControl switch { NoiseControl.Anc => "Noise cancelling", NoiseControl.Transparency => "Transparency", _ => "Off" }),
            new("Transparency level", $"{s.TransparencyLevel} / 10"),
            new("Volume (dial)", $"{HeadsetVolume} / {BaseAudioSettings.MaxVolume}"),
            new("Input", BaseInput),
            new("Firmware", FirmwareText),
        ];
    }

    partial void OnHeadsetVolumeChanged(int value)
    {
        UpdateStatusRows();
        UpdateOled();
    }

    /// <summary>Sends a setting to the base station unless the value came from the base station itself.</summary>
    void SendSetting(Action send)
    {
        if (_loadingHeadset || !BaseStationConnected)
            return;
        try
        {
            send();
            Restart(_flashDebounce);
        }
        catch (Exception e)
        {
            Toast($"Couldn't send that to the base station: {e.Message}", error: true);
        }
    }

    /// <summary>Reads the base station back and checks it holds what the app shows, then stores it in flash.</summary>
    async Task VerifyAndSaveHeadsetAsync()
    {
        try
        {
            var a = await _host.Headset.GetAudioSettingsAsync();
            var ux = await _host.Headset.GetUxSettingsAsync();
            var w = HeadsetStatus.FromWirelessSettings(await _host.Headset.QueryAsync(NovaProProtocol.WirelessSettings));
            var wrong = new List<string>();
            void Check(bool ok, string name) { if (!ok) wrong.Add(name); }
            Check(a.EqPreset == EqPreset, "EQ preset");
            if (EqPreset == 4)
                Check(EqBands.Select((b, i) => Math.Abs(b.Gain - a.CustomEqDb[i]) < 0.26).All(x => x), "EQ bands");
            Check(a.HighGain == HighGain, "high mic gain");
            Check(a.Sidetone == Sidetone, "sidetone");
            Check(a.MicVolume == MicVolume, "mic volume");
            Check(a.LineOut == LineOut, "line out");
            if (LineOut == LineOutMode.StreamMix)
                Check(a.StreamMain == StreamMain && a.StreamAux == StreamAux && a.StreamMic == StreamMic, "stream mix");
            Check(ux.Brightness == Brightness, "screen brightness");
            Check(ux.DimAfterMinutes == DimAfterMinutes, "dim timer");
            Check(ux.SimpleHomeScreen == SimpleHomeScreen, "home screen");
            Check(w.BluetoothOnAtStartup == BluetoothOnAtStartup, "Bluetooth at startup");
            Check(w.BluetoothCallMode == CallMode, "Bluetooth calls");
            Check(w.MutedMicBrightness == MutedMicBrightness, "mute light");
            Check(w.SleepAfterMinutes == SleepAfterMinutes, "sleep timer");
            Check(w.RangeMode == RangeMode, "wireless mode");
            if (wrong.Count == 0)
            {
                _host.Headset.SaveToFlash();
                Toast("Saved to the base station");
            }
            else
            {
                Toast($"The base station didn't keep: {string.Join(", ", wrong)}. Showing what it has now.", error: true);
                UpdateHeadset(w);
                await LoadHeadsetSettingsAsync();
            }
        }
        catch (Exception)
        {
            Toast("Couldn't reach the base station to check the change.", error: true);
        }
    }

    void OnEqBandChanged()
    {
        if (_loadingHeadset)
            return;
        _loadingHeadset = true;
        EqPreset = 4; // editing a band switches to Custom
        _loadingHeadset = false;
        Restart(_eqDebounce);
    }

    void ApplyEq() => SendSetting(() => _host.Headset.SetCustomEq(EqBands.Select(b => b.Gain).ToList()));

    [RelayCommand]
    void ResetEq()
    {
        foreach (var band in EqBands)
            band.Gain = 0;
    }

    partial void OnEqPresetChanged(int value) => SendSetting(() => _host.Headset.SetEqPreset(value));
    partial void OnHighGainChanged(bool value) => SendSetting(() => _host.Headset.SetHighGain(value));
    public string SidetoneText => SidetoneOptions[Math.Clamp(Sidetone, 0, 3)].Label.ToUpperInvariant();

    partial void OnSidetoneChanged(int value)
    {
        OnPropertyChanged(nameof(SidetoneText));
        SendSetting(() => _host.Headset.SetSidetone(value));
    }
    // The bars start at 0 but these settings start at 1.
    partial void OnMicVolumeChanged(int value)
    {
        if (value < 1) MicVolume = 1;
        else SendSetting(() => _host.Headset.SetMicVolume(value));
    }
    partial void OnLineOutChanged(LineOutMode value)
    {
        OnPropertyChanged(nameof(IsStreamMix));
        SendSetting(() => _host.Headset.SetLineOutMode(value));
    }
    partial void OnStreamMainChanged(int value) => SendStreamMix();
    partial void OnStreamAuxChanged(int value) => SendStreamMix();
    partial void OnStreamMicChanged(int value) => SendStreamMix();
    void SendStreamMix() => SendSetting(() => _host.Headset.SetStreamMix(StreamMain, StreamAux, StreamMic));
    partial void OnBrightnessChanged(int value)
    {
        if (value < 1) Brightness = 1;
        else SendSetting(() => _host.Headset.SetOledBrightness(value));
    }
    partial void OnDimAfterMinutesChanged(int value) => SendSetting(() => _host.Headset.SetDimTimer(value));
    partial void OnSimpleHomeScreenChanged(bool value) => SendSetting(() => _host.Headset.SetSimpleHomeScreen(value));
    partial void OnBluetoothOnAtStartupChanged(bool value) => SendSetting(() => _host.Headset.SetBluetoothOnAtStartup(value));
    partial void OnCallModeChanged(BluetoothCallMode value) => SendSetting(() => _host.Headset.SetBluetoothCallMode(value));
    partial void OnMutedMicBrightnessChanged(int value)
    {
        if (value < 1) MutedMicBrightness = 1;
        else SendSetting(() => _host.Headset.SetMutedMicBrightness(value));
    }
    partial void OnSleepAfterMinutesChanged(int value) => SendSetting(() => _host.Headset.SetSleepTimer(value));
    partial void OnRangeModeChanged(bool value) => SendSetting(() => _host.Headset.SetRangeMode(value));
    partial void OnLowBatteryWarningChanged(int value) => QueueSave();

    [RelayCommand]
    void ToggleRangeMode() => RangeMode = !RangeMode;

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
        _manageWindowsDefaults = c.ManageWindowsDefaults;
        _lowBatteryWarning = c.Headset.LowBatteryWarningPercent;
        _startWithWindows = Autostart.IsEnabled;

        // Shown channels first, in their saved order, then the hidden ones.
        ChannelToggles.Clear();
        foreach (var strip in _allStrips.OrderBy(s => c.MixerChannels.IndexOf(s.Channel) is var i and >= 0 ? i : int.MaxValue))
            ChannelToggles.Add(new ChannelToggleViewModel(strip, c.MixerChannels.Contains(strip.Channel), () =>
            {
                RebuildChannels();
                UpdateApps();
                QueueSave();
            }));
        RebuildChannels();
    }

    void QueueSave() => Restart(_saveDebounce);

    void SaveConfig()
    {
        var old = _host.Config;
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
            MixerChannels = ChannelToggles.Where(t => t.IsShown).Select(t => t.Channel).ToList(),
            Headset = new HeadsetConfig
            {
                LowBatteryWarningPercent = LowBatteryWarning,
                Screen = ScreenMode.ToString(),
                ScreenImage = SelectedScreenImage,
                ScreenSleepMinutes = ScreenSleepMinutes,
                WeatherPlace = WeatherPlace,
                WeatherFahrenheit = WeatherFahrenheit,
                WeatherAnimated = WeatherAnimated,
                CpuTemperature = CpuTemperature,
                ClockFormat = ClockFormat,
                ClockSeconds = ClockSeconds,
            },
            Channels = old.Channels,
        };
        _host.SaveConfig(config);
        if (_host.Sonar is { } sonar && IsActive)
            Try(() => sonar.SetChatMixBalance(config.ChatMixEnabled ? (float)(ChatMix / 100) : 0));
    }
}

public sealed partial class EqBandViewModel(string label, Action changed) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Gain in dB, −10…+10 in 0.5 dB steps (the base station's resolution).</summary>
    [ObservableProperty] double _gain;

    public string GainText => Gain == 0 ? "0" : Gain.ToString("+0.#;−0.#");

    partial void OnGainChanged(double value)
    {
        var snapped = Math.Round(value * 2) / 2;
        if (snapped != value)
        {
            Gain = snapped;
            return;
        }
        OnPropertyChanged(nameof(GainText));
        changed();
    }
}
