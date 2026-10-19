using System.Windows.Threading;
using AudioMixer.Core.Stats;
using AudioMixer.Core.Weather;

namespace AudioMixer.App.Services;

/// <summary>
/// Gathers what the stats and weather pages show, only while a page that uses it is chosen: PC stats once a
/// second (off the UI thread), the weather every 15 minutes, the CPU temperature from the elevated reader if
/// it's turned on. Results arrive on the UI thread.
/// </summary>
public sealed class ScreenFeeds : IDisposable
{
    static readonly TimeSpan WeatherEvery = TimeSpan.FromMinutes(15);

    readonly Dispatcher _ui;
    readonly Timer _statsTimer;
    readonly DispatcherTimer _weatherTimer;
    PcStatsReader? _reader;
    CpuTemperatureClient? _cpuTemp;
    bool _reading;
    bool _stats, _weather;
    WeatherPlace? _place;
    bool _fahrenheit;
    CancellationTokenSource? _weatherCancel;

    public event EventHandler<PcStats>? StatsChanged;

    /// <summary>A new report, or a note (e.g. an error) when there isn't one.</summary>
    public event EventHandler<(WeatherReport? Report, string? Note)>? WeatherChanged;

    public ScreenFeeds()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _statsTimer = new Timer(_ => ReadStats(), null, Timeout.Infinite, Timeout.Infinite);
        _weatherTimer = new DispatcherTimer { Interval = WeatherEvery };
        _weatherTimer.Tick += (_, _) => _ = FetchWeatherAsync();
    }

    public WeatherReport? LastWeather { get; private set; }

    /// <summary>Read stats once a second.</summary>
    public bool Stats
    {
        get => _stats;
        set
        {
            if (_stats == value)
                return;
            _stats = value;
            _statsTimer.Change(value ? TimeSpan.Zero : Timeout.InfiniteTimeSpan, value ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Show the CPU temperature (starts the elevated reader through its task).</summary>
    public bool CpuTemperature
    {
        get => _cpuTemp is not null;
        set
        {
            if (value == CpuTemperature)
                return;
            if (value)
            {
                _cpuTemp = new CpuTemperatureClient();
            }
            else
            {
                _cpuTemp?.Dispose();
                _cpuTemp = null;
            }
        }
    }

    /// <summary>True while the elevated reader is connected and sending.</summary>
    public bool CpuTemperatureConnected => _cpuTemp?.Connected == true;

    /// <summary>Fetch the weather every 15 minutes.</summary>
    public bool Weather
    {
        get => _weather;
        set
        {
            if (_weather == value)
                return;
            _weather = value;
            if (value)
            {
                _weatherTimer.Start();
                if (LastWeather is null || DateTime.UtcNow - LastWeather.FetchedUtc > WeatherEvery)
                    _ = FetchWeatherAsync();
            }
            else
            {
                _weatherTimer.Stop();
            }
        }
    }

    /// <summary>Changes where and how the weather is shown, and fetches it again.</summary>
    public void SetWeatherPlace(WeatherPlace? place, bool fahrenheit)
    {
        if (place == _place && fahrenheit == _fahrenheit)
            return;
        (_place, _fahrenheit) = (place, fahrenheit);
        LastWeather = null;
        if (_weather)
        {
            _weatherTimer.Stop();
            _weatherTimer.Start();
            _ = FetchWeatherAsync();
        }
    }

    public async Task FetchWeatherAsync()
    {
        _weatherCancel?.Cancel();
        if (_place is not { } place)
        {
            WeatherChanged?.Invoke(this, (null, "Pick a town in AudioMixer"));
            return;
        }
        var cancel = _weatherCancel = new CancellationTokenSource();
        try
        {
            var report = await OpenMeteo.GetAsync(place, _fahrenheit, cancel.Token);
            if (cancel.IsCancellationRequested)
                return;
            LastWeather = report;
            WeatherChanged?.Invoke(this, (report, null));
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            // Keep showing the last report for a while; after an hour it's too old to pass off as now.
            if (LastWeather is { } last && DateTime.UtcNow - last.FetchedUtc < TimeSpan.FromHours(1))
                return;
            LastWeather = null;
            WeatherChanged?.Invoke(this, (null, "Can't get the weather right now"));
        }
        catch (Exception) { }
    }

    void ReadStats()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            _reader ??= new PcStatsReader();
            var client = _cpuTemp;
            _reader.CpuTemperature = client is null ? null : () => client.Value;
            var stats = _reader.Read();
            _ui.BeginInvoke(() =>
            {
                if (_stats)
                    StatsChanged?.Invoke(this, stats);
            });
        }
        catch (Exception) { }
        finally
        {
            _reading = false;
        }
    }

    public void Dispose()
    {
        _statsTimer.Dispose();
        _weatherTimer.Stop();
        _weatherCancel?.Cancel();
        _cpuTemp?.Dispose();
        _reader?.Dispose();
    }
}
