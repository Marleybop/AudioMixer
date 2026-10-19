using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace AudioMixer.Core.Weather;

/// <summary>A place to show the weather for. <see cref="Name"/> is what the user sees, e.g. "Calgary, Alberta, Canada".</summary>
public sealed record WeatherPlace(string Name, double Latitude, double Longitude)
{
    public override string ToString() => Name;

    /// <summary>The town alone, for the small screen.</summary>
    public string ShortName => Name.Split(',')[0].Trim();
}

/// <summary>Current conditions and today's range. Temperatures are in the unit asked for.</summary>
public sealed record WeatherReport(
    DateTime FetchedUtc,
    double Temperature,
    double FeelsLike,
    int Code,
    bool IsDay,
    double High,
    double Low,
    int? RainChance,
    double Wind,
    bool Fahrenheit)
{
    public string Description => OpenMeteo.Describe(Code);
}

/// <summary>
/// Weather from Open-Meteo (open-meteo.com): free, no API key, CC BY 4.0 data. Its free API is for non-commercial
/// use and under 10,000 calls a day; AudioMixer asks once every 15 minutes. Credit "Weather data by Open-Meteo.com"
/// wherever the data is shown.
/// </summary>
public static class OpenMeteo
{
    public const string Credit = "Weather data by Open-Meteo.com";
    public const string CreditUrl = "https://open-meteo.com/";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static OpenMeteo() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioMixer/0.1 (+https://github.com/Marleybop/AudioMixer)");

    /// <summary>Places matching a town or city name, best match first.</summary>
    public static async Task<IReadOnlyList<WeatherPlace>> SearchAsync(string text, CancellationToken cancel = default)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(text.Trim())}&count=8&language=en&format=json";
        using var json = JsonDocument.Parse(await Http.GetStringAsync(url, cancel));
        if (!json.RootElement.TryGetProperty("results", out var results))
            return [];
        return results.EnumerateArray().Select(r => new WeatherPlace(
                string.Join(", ", new[] { Str(r, "name"), Str(r, "admin1"), Str(r, "country") }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
                r.GetProperty("latitude").GetDouble(),
                r.GetProperty("longitude").GetDouble()))
            .ToList();
    }

    public static async Task<WeatherReport> GetAsync(WeatherPlace place, bool fahrenheit, CancellationToken cancel = default)
    {
        var url = "https://api.open-meteo.com/v1/forecast"
                  + $"?latitude={place.Latitude.ToString(CultureInfo.InvariantCulture)}&longitude={place.Longitude.ToString(CultureInfo.InvariantCulture)}"
                  + "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m"
                  + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max"
                  + "&timezone=auto&forecast_days=1"
                  + (fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "&wind_speed_unit=kmh");
        using var json = JsonDocument.Parse(await Http.GetStringAsync(url, cancel));
        var current = json.RootElement.GetProperty("current");
        var daily = json.RootElement.GetProperty("daily");
        return new WeatherReport(
            DateTime.UtcNow,
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1,
            First(daily, "temperature_2m_max") ?? current.GetProperty("temperature_2m").GetDouble(),
            First(daily, "temperature_2m_min") ?? current.GetProperty("temperature_2m").GetDouble(),
            First(daily, "precipitation_probability_max") is { } p ? (int)Math.Round(p) : null,
            current.GetProperty("wind_speed_10m").GetDouble(),
            fahrenheit);
    }

    /// <summary>WMO weather code in a few words.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunder and hail",
        _ => "",
    };

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double? First(JsonElement daily, string name) =>
        daily.TryGetProperty(name, out var a) && a.GetArrayLength() > 0 && a[0].ValueKind == JsonValueKind.Number ? a[0].GetDouble() : null;
}
