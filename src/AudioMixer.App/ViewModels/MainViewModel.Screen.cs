using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using AudioMixer.App.Services;
using AudioMixer.Core.Config;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Stats;
using AudioMixer.Core.Weather;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioMixer.App.ViewModels;

/// <summary>One thing the base station's screen can show, with a line saying what it is.</summary>
public sealed record ScreenChoice(ScreenMode Value, string Label, string Hint)
{
    public override string ToString() => Label;
}

// ===== Base station screen =====
public sealed partial class MainViewModel
{
    readonly ScreenFeeds _feeds = new();

    public static IReadOnlyList<ScreenChoice> ScreenChoices { get; } =
    [
        new(ScreenMode.BaseStation, "Normal display", "The base station's own screen"),
        new(ScreenMode.Off, "Off", "Blank while AudioMixer runs"),
        new(ScreenMode.Widget, "Headset widget", "Battery, mic and volume"),
        new(ScreenMode.Stats, "PC stats", "CPU and GPU load and temperature, memory"),
        new(ScreenMode.Weather, "Weather", "Now and today, for your town"),
        new(ScreenMode.Cycle, "Take turns", $"Widget, stats and weather, {OledWidget.CycleTime.TotalSeconds:0} s each"),
        new(ScreenMode.Custom, "Picture or animation", "PNG, JPG or GIF from your Screens folder"),
    ];

    public static IReadOnlyList<Option> ScreenSleepOptions { get; } =
        [new(0, "Never"), new(5, "5 min"), new(15, "15 min"), new(30, "30 min"), new(60, "1 hour"), new(120, "2 hours")];

    public static IReadOnlyList<Option> TemperatureUnitOptions { get; } = [new(false, "°C"), new(true, "°F")];

    public static string WeatherCredit => OpenMeteo.Credit;
    public static string WeatherCreditUrl => OpenMeteo.CreditUrl;
    public static string PawnIOUrl => Core.Stats.CpuTemperature.PawnIOUrl;

    void InitScreen(HeadsetConfig config)
    {
        _oled.SleepChanged += (_, _) => OnPropertyChanged(nameof(ScreenSleepText));
        _feeds.StatsChanged += (_, stats) =>
        {
            _oled.UpdateStats(stats);
            OnPropertyChanged(nameof(CpuTemperatureStatus));
        };
        _feeds.WeatherChanged += (_, w) =>
        {
            _oled.UpdateWeather(w.Report, WeatherPlace?.ShortName, w.Note);
            OnPropertyChanged(nameof(WeatherStatus));
        };

        _screenMode = Enum.TryParse<ScreenMode>(config.Screen, out var mode) ? mode : ScreenMode.BaseStation;
        _screenSleepMinutes = config.ScreenSleepMinutes;
        _oled.SleepAfter = _screenSleepMinutes > 0 ? TimeSpan.FromMinutes(_screenSleepMinutes) : null;
        _weatherPlace = config.WeatherPlace;
        _weatherFahrenheit = config.WeatherFahrenheit;
        _weatherAnimated = config.WeatherAnimated;
        _oled.AnimateWeather = _weatherAnimated;
        _cpuTemperature = config.CpuTemperature && Core.Stats.CpuTemperature.PawnIOInstalled;
        _feeds.CpuTemperature = _cpuTemperature;
        _feeds.SetWeatherPlace(_weatherPlace, _weatherFahrenheit);
        _oled.UpdateWeather(null, _weatherPlace?.ShortName, _weatherPlace is null ? "Pick a town in AudioMixer" : null);
        RefreshScreenImages();
        _selectedScreenImage = ScreenImages.FirstOrDefault(n => n == config.ScreenImage) ?? ScreenImages.FirstOrDefault();
        _oled.LoadCustom(ScreenImagePath(_selectedScreenImage));
        WatchScreensFolder();
        UpdateOled();
        _oled.Mode = _screenMode;
        UpdateFeeds();
    }

    [ObservableProperty] ScreenMode _screenMode;
    [ObservableProperty] System.Windows.Media.ImageSource? _oledPreview;
    [ObservableProperty] int _screenSleepMinutes;

    public bool IsCustomScreen => ScreenMode == ScreenMode.Custom;
    public bool ShowsStats => ScreenMode is ScreenMode.Stats or ScreenMode.Cycle;
    public bool ShowsWeather => ScreenMode is ScreenMode.Weather or ScreenMode.Cycle;
    public string ScreenSleepText => _oled.IsAsleep ? "Asleep now. Use the keyboard, mouse or dial to wake it." : "";

    partial void OnScreenModeChanged(ScreenMode value)
    {
        OnPropertyChanged(nameof(IsCustomScreen));
        OnPropertyChanged(nameof(ShowsStats));
        OnPropertyChanged(nameof(ShowsWeather));
        _oled.Mode = value;
        UpdateFeeds();
        QueueSave();
        Toast(value switch
        {
            ScreenMode.Widget => "The base station now shows battery, mic and volume",
            ScreenMode.Stats => "The base station now shows your PC's stats",
            ScreenMode.Weather => WeatherPlace is null ? "Pick a town for the weather" : $"The base station now shows the weather in {WeatherPlace.ShortName}",
            ScreenMode.Cycle => "The base station now takes turns: widget, stats and weather",
            ScreenMode.Off => "The base station's screen is off while AudioMixer runs",
            ScreenMode.Custom => ScreenImages.Count == 0 ? "Put a picture or GIF in the Screens folder first" : $"The base station now shows {SelectedScreenImage}",
            _ => "The base station shows its own screen again",
        });
    }

    /// <summary>Only read stats and the weather while a page shows them.</summary>
    void UpdateFeeds()
    {
        _feeds.Stats = ShowsStats;
        _feeds.Weather = ShowsWeather;
    }

    partial void OnScreenSleepMinutesChanged(int value)
    {
        _oled.SleepAfter = value > 0 ? TimeSpan.FromMinutes(value) : null;
        QueueSave();
        Toast(value > 0 ? $"The base station's screen turns off after {ScreenSleepOptions.First(o => (int)o.Value == value).Label} without use" : "Screen sleep off");
    }

    void UpdateOled() => _oled.Update(new WidgetState(HeadsetOn, HeadsetBattery, SpareBattery,
        _status?.Charge == ChargeState.Charging, IsMicSilenced, HeadsetVolume, BaseAudioSettings.MaxVolume));

    /// <summary>Hands the base station's screen back when AudioMixer quits.</summary>
    public void Shutdown()
    {
        _feeds.Dispose();
        _oled.Release();
    }

    // ---- Pictures ----

    /// <summary>File names in the Screens folder.</summary>
    public ObservableCollection<string> ScreenImages { get; } = [];
    [ObservableProperty] string? _selectedScreenImage;
    FileSystemWatcher? _screensWatcher;

    static string? ScreenImagePath(string? name) => name is null ? null : Path.Combine(OledWidget.ScreensFolder, name);

    void RefreshScreenImages()
    {
        var names = Directory.Exists(OledWidget.ScreensFolder)
            ? Directory.EnumerateFiles(OledWidget.ScreensFolder)
                .Where(f => OledWidget.ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
        if (names.SequenceEqual(ScreenImages))
            return;
        var selected = SelectedScreenImage;
        ScreenImages.Clear();
        foreach (var n in names)
            ScreenImages.Add(n);
        SelectedScreenImage = names.Contains(selected ?? "") ? selected : names.FirstOrDefault();
    }

    void WatchScreensFolder()
    {
        try
        {
            Directory.CreateDirectory(OledWidget.ScreensFolder);
            _screensWatcher = new FileSystemWatcher(OledWidget.ScreensFolder) { EnableRaisingEvents = true };
            FileSystemEventHandler changed = (_, e) => _ui.BeginInvoke(() =>
            {
                RefreshScreenImages();
                // A picture that is being used was replaced: show the new one.
                if (string.Equals(e.Name, SelectedScreenImage, StringComparison.OrdinalIgnoreCase))
                    _oled.LoadCustom(ScreenImagePath(SelectedScreenImage));
            });
            _screensWatcher.Created += changed;
            _screensWatcher.Deleted += changed;
            _screensWatcher.Changed += changed;
            _screensWatcher.Renamed += (_, _) => _ui.BeginInvoke(RefreshScreenImages);
        }
        catch (Exception) { }
    }

    partial void OnSelectedScreenImageChanged(string? value)
    {
        _oled.LoadCustom(ScreenImagePath(value), error => Toast(error, error: true));
        QueueSave();
    }

    [RelayCommand]
    void OpenScreensFolder()
    {
        Directory.CreateDirectory(OledWidget.ScreensFolder);
        Process.Start(new ProcessStartInfo(OledWidget.ScreensFolder) { UseShellExecute = true });
    }

    // ---- Weather ----

    [ObservableProperty] WeatherPlace? _weatherPlace;
    [ObservableProperty] bool _weatherFahrenheit;
    [ObservableProperty] bool _weatherAnimated;
    [ObservableProperty] string _weatherSearch = "";
    [ObservableProperty] bool _searchingWeather;

    /// <summary>Towns matching the last search, to pick from.</summary>
    public ObservableCollection<WeatherPlace> WeatherResults { get; } = [];

    public bool HasWeatherResults => WeatherResults.Count > 0;

    public string WeatherStatus => _feeds.LastWeather is { } w
        ? $"{Degrees(w.Temperature, w.Fahrenheit)}, {w.Description.ToLowerInvariant()} (updated {w.FetchedUtc.ToLocalTime():t})"
        : WeatherPlace is null ? "No town picked yet." : ShowsWeather ? "Getting the weather…" : "";

    static string Degrees(double value, bool fahrenheit) => $"{Math.Round(value):0}°{(fahrenheit ? "F" : "C")}";

    [RelayCommand]
    async Task SearchWeatherAsync()
    {
        if (string.IsNullOrWhiteSpace(WeatherSearch))
            return;
        SearchingWeather = true;
        try
        {
            var places = await OpenMeteo.SearchAsync(WeatherSearch);
            WeatherResults.Clear();
            foreach (var p in places)
                WeatherResults.Add(p);
            OnPropertyChanged(nameof(HasWeatherResults));
            if (places.Count == 0)
                Toast($"No town called \"{WeatherSearch.Trim()}\" found", error: true);
        }
        catch (Exception)
        {
            Toast("Couldn't search for towns. Are you online?", error: true);
        }
        finally
        {
            SearchingWeather = false;
        }
    }

    [RelayCommand]
    void PickWeatherPlace(WeatherPlace? place)
    {
        if (place is null)
            return;
        WeatherPlace = place;
        WeatherResults.Clear();
        OnPropertyChanged(nameof(HasWeatherResults));
        WeatherSearch = "";
    }

    partial void OnWeatherPlaceChanged(WeatherPlace? value) => WeatherSettingsChanged(value is null ? "Weather town cleared" : $"Weather for {value.Name}");

    partial void OnWeatherFahrenheitChanged(bool value) => WeatherSettingsChanged(value ? "Weather in °F" : "Weather in °C");

    partial void OnWeatherAnimatedChanged(bool value)
    {
        _oled.AnimateWeather = value;
        QueueSave();
        Toast(value ? "The weather page now shows falling snow, rain and the like" : "The weather page stays still");
    }

    void WeatherSettingsChanged(string toast)
    {
        _feeds.SetWeatherPlace(WeatherPlace, WeatherFahrenheit);
        _oled.UpdateWeather(null, WeatherPlace?.ShortName, WeatherPlace is null ? "Pick a town in AudioMixer" : null);
        OnPropertyChanged(nameof(WeatherStatus));
        QueueSave();
        Toast(toast);
    }

    // ---- CPU temperature ----

    [ObservableProperty] bool _cpuTemperature;
    bool _settingUpCpuTemperature;

    public string CpuTemperatureStatus =>
        !CpuTemperature ? (Core.Stats.CpuTemperature.PawnIOInstalled ? "Off." : "Needs the free PawnIO driver first.")
        : _feeds.CpuTemperatureConnected ? "On."
        : "Starting the reader…";

    partial void OnCpuTemperatureChanged(bool value)
    {
        if (_settingUpCpuTemperature)
            return;
        if (value)
            _ = TurnOnCpuTemperatureAsync();
        else
        {
            _feeds.CpuTemperature = false;
            OnPropertyChanged(nameof(CpuTemperatureStatus));
            QueueSave();
        }
    }

    /// <summary>
    /// Checks PawnIO is there and the reader's task runs a copy of this version of AudioMixer; if not, sets it up
    /// (one admin prompt). Switches back off if that doesn't work out.
    /// </summary>
    async Task TurnOnCpuTemperatureAsync()
    {
        void Fail(string message)
        {
            _settingUpCpuTemperature = true;
            CpuTemperature = false;
            _settingUpCpuTemperature = false;
            OnPropertyChanged(nameof(CpuTemperatureStatus));
            Toast(message, error: true);
        }

        if (!Core.Stats.CpuTemperature.PawnIOInstalled)
        {
            Fail("Install the free PawnIO driver first (link below), then turn this on again");
            return;
        }
        var exe = Environment.ProcessPath!;
        if (!await Task.Run(() => Core.Stats.CpuTemperature.TaskIsCurrent(AppContext.BaseDirectory, Path.GetFileName(exe))))
        {
            try
            {
                using var setup = Process.Start(new ProcessStartInfo(exe, "--install-sensors") { UseShellExecute = true, Verb = "runas" })!;
                await setup.WaitForExitAsync();
                if (setup.ExitCode != 0)
                {
                    Fail("Couldn't set up the CPU temperature reader");
                    return;
                }
            }
            catch (Win32Exception)
            {
                Fail("The CPU temperature needs one admin prompt to set up");
                return;
            }
        }
        _feeds.CpuTemperature = true;
        OnPropertyChanged(nameof(CpuTemperatureStatus));
        QueueSave();
        Toast("CPU temperature on");
    }
}
