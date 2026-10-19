using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Media;
using AudioMixer.App.Controls;
using AudioMixer.Core.Config;
using AudioMixer.Core.Sonar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioMixer.App.ViewModels;

/// <summary>A preset in the picker.</summary>
public sealed record PresetItem(string Name, string Group, SoundSettings Settings, bool IsYours)
{
    public override string ToString() => Name;
}

/// <summary>
/// One channel's EQ and effects, as on Sonar's channel pages. Every change is applied to the Sonar engine at once
/// and remembered in the config.
/// </summary>
public sealed partial class ChannelSoundViewModel : ObservableObject
{
    public static IReadOnlyList<Option> FilterTypeOptions { get; } =
    [
        new(FilterType.Peaking, "Peak"), new(FilterType.LowShelf, "Low shelf"), new(FilterType.HighShelf, "High shelf"),
        new(FilterType.LowPass, "Low pass"), new(FilterType.HighPass, "High pass"), new(FilterType.Notch, "Notch"),
    ];
    public static IReadOnlyList<Option> SpatialModeOptions { get; } = [new(SpatialMode.Headphones, "Headphones"), new(SpatialMode.Speakers, "Speakers")];
    public static IReadOnlyList<Option> LoudnessOptions { get; } = [new(Loudness.Quiet, "Quiet"), new(Loudness.Balanced, "Balanced"), new(Loudness.Loud, "Loud")];

    readonly ChannelConfig _config;
    readonly Action<Channel, SoundSettings> _apply;
    readonly Action _save;
    readonly PresetStore _store;
    readonly IReadOnlyList<SoundPreset> _sonarPresets;
    SoundSettings _s;
    /// <summary>The preset's sound as JSON; the channel counts as edited while it differs.</summary>
    string _baseline;
    bool _loading;

    public Channel Channel { get; }
    public string Name { get; }
    public Brush Color { get; }
    public bool IsVoice => SoundSettings.IsVoice(Channel);
    public bool IsPlayback => !IsVoice;
    public bool IsMic => Channel == Channel.Mic;

    public ObservableCollection<EqBandEditViewModel> Bands { get; } = [];

    /// <summary>A short confirmation for the user (shown as a toast).</summary>
    public event EventHandler<string>? Notice;
    [ObservableProperty] EqBandEditViewModel? _selectedBand;

    public ChannelSoundViewModel(Channel channel, string name, Brush color, ChannelConfig config, PresetStore store,
        IReadOnlyList<SoundPreset> sonarPresets, Action<Channel, SoundSettings> apply, Action save)
    {
        Channel = channel;
        Name = name;
        Color = color;
        _config = config;
        _store = store;
        _sonarPresets = sonarPresets;
        _apply = apply;
        _save = save;
        _s = config.Sound ?? SoundSettings.Flat(channel);
        _baseline = !config.Modified ? _s.ToJson()
            : (store.For(channel).Concat(sonarPresets.Where(p => p.Channel == channel)).FirstOrDefault(p => p.Name == config.PresetName)?.Settings.ToJson() ?? "");
        RebuildBands();
        RefreshPresets();
    }

    /// <summary>Writes the whole sound to the engine (after taking over, or when the engine may be out of step).</summary>
    public void ApplyAll() => _apply(Channel, _s);

    void RebuildBands()
    {
        var selected = SelectedBand?.Number;
        Bands.Clear();
        for (var i = 0; i < _s.ParametricEQ.Filters.Length; i++)
            Bands.Add(new EqBandEditViewModel(i + 1, _s.ParametricEQ.Filters[i], Changed));
        SelectedBand = Bands.FirstOrDefault(b => b.Number == selected) ?? Bands.FirstOrDefault(b => b.Enabled) ?? Bands.FirstOrDefault();
    }

    /// <summary>Called on every edit: apply, mark as changed from the preset, save soon.</summary>
    void Changed()
    {
        if (_loading)
            return;
        _apply(Channel, _s);
        _config.Sound = _s;
        var modified = _s.ToJson() != _baseline;
        if (_config.Modified != modified)
        {
            _config.Modified = modified;
            OnPropertyChanged(nameof(Modified));
            OnPropertyChanged(nameof(PresetLabel));
        }
        OnPropertyChanged(nameof(BoostFilters));
        _save();
    }

    bool Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return false;
        assign(value);
        OnPropertyChanged(name);
        Changed();
        return true;
    }

    // ===== Preset =====

    public string PresetName => _config.PresetName;
    public bool Modified => _config.Modified;
    /// <summary>What the channel strip shows: the preset, with a dot once you've tweaked it.</summary>
    public string PresetLabel => !_s.ParametricEQ.Enabled && !AnyEffectOn ? "Off" : Modified ? $"{PresetName} •" : PresetName;

    bool AnyEffectOn => IsVoice
        ? _s.NoiseCancelingState.Enabled || _s.NoiseReductionState.Enabled || _s.ImpactNoiseReductionState.Enabled || _s.NoiseGateState.Enabled || _s.VolumeStabilizerState.Enabled
        : _s.VirtualSurroundState || _s.SmartVolume.Enabled || _s.GeneralGain > 0;

    [ObservableProperty] string _presetFilter = "";
    /// <summary>The picker's list, grouped into yours / basic / from Sonar.</summary>
    [ObservableProperty] ListCollectionView _presets = new(new List<PresetItem>());
    [ObservableProperty] PresetItem? _selectedPreset;
    [ObservableProperty] string _newPresetName = "";

    partial void OnPresetFilterChanged(string value) => RefreshPresets();

    void RefreshPresets()
    {
        var filter = PresetFilter.Trim();
        bool Match(string n) => filter.Length == 0 || n.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        var list = new List<PresetItem>();
        list.AddRange(_store.For(Channel).Where(p => Match(p.Name)).OrderBy(p => p.Name)
            .Select(p => new PresetItem(p.Name, "YOUR PRESETS", p.Settings, IsYours: true)));
        list.Add(new PresetItem("Flat", "BASIC", SoundSettings.Flat(Channel), IsYours: false));
        list.AddRange(_sonarPresets.Where(p => p.Channel == Channel && p.Name != "Flat" && Match(p.Name))
            .OrderBy(p => p.Name == "Custom" ? 0 : 1).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PresetItem(p.Name == "Custom" ? "Custom (from Sonar)" : p.Name, "FROM SONAR", p.Settings, IsYours: false)));
        if (filter.Length > 0)
            list.RemoveAll(p => p.Group == "BASIC" && !Match(p.Name));
        var view = new ListCollectionView(list);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PresetItem.Group)));
        Presets = view;
    }

    [RelayCommand]
    void ApplyPreset(PresetItem? preset)
    {
        if (preset is null)
            return;
        Load(preset.Settings.Clone(), preset.Name, modified: false);
        Notice?.Invoke(this, $"{Name}: “{preset.Name}” applied");
    }

    [RelayCommand]
    void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0)
            return;
        _store.Save(name, Channel, _s);
        _baseline = _s.ToJson();
        _config.PresetName = name;
        _config.Modified = false;
        NewPresetName = "";
        RefreshPresets();
        RaisePreset();
        _save();
        Notice?.Invoke(this, $"Saved “{name}”. It's in Your presets for every {(IsVoice ? "voice" : "playback")} channel.");
    }

    [RelayCommand]
    void DeletePreset(PresetItem? preset)
    {
        if (preset is not { IsYours: true })
            return;
        _store.Delete(preset.Name, Channel);
        RefreshPresets();
        Notice?.Invoke(this, $"Deleted “{preset.Name}”");
    }

    [RelayCommand]
    void ResetFlat()
    {
        Load(SoundSettings.Flat(Channel), "Flat", modified: false);
        Notice?.Invoke(this, $"{Name} reset to Flat");
    }

    /// <summary>Replaces the whole sound (a preset was picked).</summary>
    public void Load(SoundSettings settings, string presetName, bool modified)
    {
        _s = settings;
        _baseline = modified ? "" : settings.ToJson();
        _config.Sound = settings;
        _config.PresetName = presetName;
        _config.Modified = modified;
        _loading = true;
        RebuildBands();
        _loading = false;
        OnPropertyChanged(string.Empty);
        _apply(Channel, _s);
        _save();
    }

    void RaisePreset()
    {
        OnPropertyChanged(nameof(PresetName));
        OnPropertyChanged(nameof(Modified));
        OnPropertyChanged(nameof(PresetLabel));
    }

    // ===== EQ =====

    public bool EqEnabled
    {
        get => _s.ParametricEQ.Enabled;
        set
        {
            if (!Set(_s.ParametricEQ.Enabled, value, v => _s.ParametricEQ.Enabled = v))
                return;
            // Sonar switches the boosts with the EQ.
            if (IsPlayback)
            {
                _s.BassBoostState.Enabled = _s.VoiceClarityState.Enabled = _s.TrebleBoostState.Enabled = value;
                Changed();
            }
            OnPropertyChanged(nameof(PresetLabel));
        }
    }

    // Sonar's boosts are fixed filters inside the APO: bass 80 Hz, voice 2 kHz, treble 8 kHz.
    public IReadOnlyList<EqFilter> BoostFilters => IsVoice || !_s.ParametricEQ.Enabled ? [] :
    [
        new EqFilter(_s.BassBoostState.Enabled, 80, FilterType.Peaking, _s.BassBoostState.Value, 2.0f),
        new EqFilter(_s.VoiceClarityState.Enabled, 2000, FilterType.Peaking, _s.VoiceClarityState.Value, 2.87f),
        new EqFilter(_s.TrebleBoostState.Enabled, 8000, FilterType.Peaking, _s.TrebleBoostState.Value, 2.0f),
    ];

    public double BassBoost { get => _s.BassBoostState.Value; set => Set(_s.BassBoostState.Value, (float)Math.Round(value, 1), v => _s.BassBoostState.Value = v); }
    public double VoiceBoost { get => _s.VoiceClarityState.Value; set => Set(_s.VoiceClarityState.Value, (float)Math.Round(value, 1), v => _s.VoiceClarityState.Value = v); }
    public double TrebleBoost { get => _s.TrebleBoostState.Value; set => Set(_s.TrebleBoostState.Value, (float)Math.Round(value, 1), v => _s.TrebleBoostState.Value = v); }

    [RelayCommand]
    void FlattenBands()
    {
        foreach (var band in Bands)
            band.Gain = 0;
        BassBoost = VoiceBoost = TrebleBoost = 0;
    }

    // ===== Playback effects =====

    public bool SpatialOn { get => _s.VirtualSurroundState; set { if (Set(_s.VirtualSurroundState, value, v => _s.VirtualSurroundState = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public SpatialMode SpatialMode { get => _s.FormFactor; set => Set(_s.FormFactor, value, v => _s.FormFactor = v); }
    /// <summary>−12 (performance) … 0 (immersion).</summary>
    public double SpatialTuning { get => _s.ReverbGainDB; set => Set(_s.ReverbGainDB, (float)Math.Round(value, 1), v => _s.ReverbGainDB = v); }

    /// <summary>Speaker distance as Sonar shows it: 0…100 %, set as gain on every speaker but the sub.</summary>
    public double SpatialDistance
    {
        get => Math.Round(100 - 100 * _s.VirtualSurroundChannels.Center.Gain / 12);
        set
        {
            var gain = (float)Math.Round(12 - 12 * Math.Clamp(value, 0, 100) / 100, 2);
            var speakers = _s.VirtualSurroundChannels;
            if (speakers.Center.Gain == gain)
                return;
            foreach (var s in speakers.All.Where(s => s != speakers.SubWoofer))
                s.Gain = gain;
            OnPropertyChanged();
            Changed();
        }
    }

    public double VolumeBoost { get => _s.GeneralGain; set { if (Set(_s.GeneralGain, (float)Math.Round(value, 1), v => _s.GeneralGain = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public bool SmartVolumeOn { get => _s.SmartVolume.Enabled; set { if (Set(_s.SmartVolume.Enabled, value, v => _s.SmartVolume.Enabled = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public double SmartVolumeLevel { get => Math.Round(_s.SmartVolume.VolumeLevel * 100); set => Set(_s.SmartVolume.VolumeLevel, (float)Math.Round(value / 100, 2), v => _s.SmartVolume.VolumeLevel = v); }
    public Loudness Loudness { get => _s.SmartVolume.Loudness; set => Set(_s.SmartVolume.Loudness, value, v => _s.SmartVolume.Loudness = v); }

    // ===== Voice effects =====

    public bool NoiseCancelOn
    {
        get => _s.NoiseCancelingState.Enabled;
        set
        {
            if (!Set(_s.NoiseCancelingState.Enabled, value, v => _s.NoiseCancelingState.Enabled = v))
                return;
            OnPropertyChanged(nameof(ClassicNoiseReductionAvailable));
            OnPropertyChanged(nameof(PresetLabel));
        }
    }
    public double NoiseCancelStrength { get => Math.Round(_s.NoiseCancelingState.Value * 100); set => Set(_s.NoiseCancelingState.Value, (float)Math.Round(value / 100, 2), v => _s.NoiseCancelingState.Value = v); }
    public bool EchoCancelOn { get => _s.AcousticEchoCancelingState; set => Set(_s.AcousticEchoCancelingState, value, v => _s.AcousticEchoCancelingState = v); }

    /// <summary>ClearCast replaces the classic background/impact reduction while it's on (as in Sonar).</summary>
    public bool ClassicNoiseReductionAvailable => !_s.NoiseCancelingState.Enabled;

    public bool NoiseReductionOn { get => _s.NoiseReductionState.Enabled; set { if (Set(_s.NoiseReductionState.Enabled, value, v => _s.NoiseReductionState.Enabled = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public double NoiseReductionLevel { get => Math.Round(_s.NoiseReductionState.Value * 100); set => Set(_s.NoiseReductionState.Value, (float)Math.Round(value / 100, 2), v => _s.NoiseReductionState.Value = v); }
    public bool ImpactReductionOn { get => _s.ImpactNoiseReductionState.Enabled; set { if (Set(_s.ImpactNoiseReductionState.Enabled, value, v => _s.ImpactNoiseReductionState.Enabled = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public double ImpactReductionLevel { get => Math.Round(_s.ImpactNoiseReductionState.Value * 100); set => Set(_s.ImpactNoiseReductionState.Value, (float)Math.Round(value / 100, 2), v => _s.ImpactNoiseReductionState.Value = v); }

    public bool NoiseGateOn { get => _s.NoiseGateState.Enabled; set { if (Set(_s.NoiseGateState.Enabled, value, v => _s.NoiseGateState.Enabled = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    /// <summary>−60…−10 dB.</summary>
    public double NoiseGateThreshold { get => _s.NoiseGateState.Value; set => Set(_s.NoiseGateState.Value, (float)Math.Round(Math.Clamp(value, -60, -10), 1), v => _s.NoiseGateState.Value = v); }
    public bool NoiseGateAuto
    {
        get => _s.AutomaticNoiseGateState.Enabled;
        set { if (Set(_s.AutomaticNoiseGateState.Enabled, value, v => _s.AutomaticNoiseGateState.Enabled = v)) OnPropertyChanged(nameof(NoiseGateManual)); }
    }
    public bool NoiseGateManual => !NoiseGateAuto;

    public bool CompressorOn { get => _s.VolumeStabilizerState.Enabled; set { if (Set(_s.VolumeStabilizerState.Enabled, value, v => _s.VolumeStabilizerState.Enabled = v)) OnPropertyChanged(nameof(PresetLabel)); } }
    public double CompressorLevel { get => Math.Round(_s.VolumeStabilizerState.Value * 100); set => Set(_s.VolumeStabilizerState.Value, (float)Math.Round(value / 100, 2), v => _s.VolumeStabilizerState.Value = v); }
}

/// <summary>One parametric EQ band, editable from the graph or the band panel.</summary>
public sealed class EqBandEditViewModel : ObservableObject, IEqBand
{
    readonly EqFilter _f;
    readonly Action _changed;

    public EqBandEditViewModel(int number, EqFilter filter, Action changed)
    {
        Number = number;
        _f = filter;
        _changed = changed;
    }

    public int Number { get; }

    public bool Enabled { get => _f.Enabled; set { if (_f.Enabled == value) return; _f.Enabled = value; Raise(); } }
    public FilterType Type { get => _f.Type; set { if (_f.Type == value) return; _f.Type = value; Raise(nameof(Type), nameof(HasGain)); } }
    public bool HasGain => Type is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf;

    public double Frequency
    {
        get => _f.Frequency;
        set
        {
            var v = (float)Math.Round(Math.Clamp(value, EqGraph.MinFreq, EqGraph.MaxFreq));
            if (_f.Frequency == v) return;
            _f.Frequency = v;
            Raise(nameof(Frequency), nameof(FrequencyText), nameof(FrequencyPosition));
        }
    }

    public double Gain
    {
        get => _f.Gain;
        set
        {
            var v = (float)Math.Round(Math.Clamp(value, -EqGraph.MaxGain, EqGraph.MaxGain), 1);
            if (_f.Gain == v) return;
            _f.Gain = v;
            Raise(nameof(Gain), nameof(GainText));
        }
    }

    public double Q
    {
        get => _f.QFactor;
        set
        {
            var v = (float)Math.Round(Math.Clamp(value, EqGraph.MinQ, EqGraph.MaxQ), 2);
            if (_f.QFactor == v) return;
            _f.QFactor = v;
            Raise(nameof(Q), nameof(QText), nameof(QPosition));
        }
    }

    // Bars are linear, so frequency and Q are edited on a log scale through these 0…1000 positions.
    public double FrequencyPosition
    {
        get => Math.Log(Frequency / EqGraph.MinFreq) / Math.Log(EqGraph.MaxFreq / EqGraph.MinFreq) * 1000;
        set => Frequency = EqGraph.MinFreq * Math.Pow(EqGraph.MaxFreq / EqGraph.MinFreq, value / 1000);
    }

    public double QPosition
    {
        get => Math.Log(Q / EqGraph.MinQ) / Math.Log(EqGraph.MaxQ / EqGraph.MinQ) * 1000;
        set => Q = EqGraph.MinQ * Math.Pow(EqGraph.MaxQ / EqGraph.MinQ, value / 1000);
    }

    /// <summary>Bar position of Q = 0.707 (a double-click on the width bar returns to it).</summary>
    public static double DefaultQPosition { get; } = Math.Log(0.7071 / EqGraph.MinQ) / Math.Log(EqGraph.MaxQ / EqGraph.MinQ) * 1000;

    public string FrequencyText => Frequency >= 1000 ? $"{Frequency / 1000:0.##} kHz" : $"{Frequency:0} Hz";
    public string GainText => Gain == 0 ? "0 dB" : Gain.ToString("+0.0;−0.0") + " dB";
    public string QText => Q.ToString("0.00");

    void Raise(params string[] names)
    {
        foreach (var n in names.Length == 0 ? new[] { nameof(Enabled) } : names)
            OnPropertyChanged(n);
        _changed();
    }
}
