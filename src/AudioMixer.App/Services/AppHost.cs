using AudioMixer.Core.Audio;
using AudioMixer.Core.Config;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Routing;
using AudioMixer.Core.Sonar;

namespace AudioMixer.App.Services;

/// <summary>
/// Owns the engine for the lifetime of the app: device enumeration, the base station link, the Sonar engine
/// and the router. The router only runs once Sonar's own app is out of the way, otherwise the two would
/// fight over routing and the microphone.
/// </summary>
public sealed class AppHost : IDisposable
{
    public AudioEndpoints Endpoints { get; } = new();
    public NovaProWireless Headset { get; } = new();
    public SonarEngine? Sonar { get; }
    public string? SonarError { get; }
    public AppConfig Config { get; private set; }
    public Router? Router { get; private set; }

    /// <summary>Raised when the router starts or stops (e.g. after taking over from GG).</summary>
    public event EventHandler? ActiveChanged;

    public bool IsActive => Router is not null;

    public AppHost()
    {
        Config = AppConfig.Load();
        Sonar = SonarEngine.TryCreate(out var error);
        SonarError = error;
        Headset.ConnectedChanged += (_, connected) =>
        {
            if (connected && IsActive)
                EnableBaseStationSoftwareMode(true);
        };
        Headset.TryConnect();
    }

    /// <summary>Starts managing audio, unless Sonar's app is still running (then <see cref="TakeOverFromGG"/> first).</summary>
    public bool TryActivate()
    {
        if (IsActive)
            return true;
        if (SteelSeriesGG.IsSonarRunning)
            return false;
        Router = new Router(Endpoints, Headset, Sonar, Config);
        EnableBaseStationSoftwareMode(true);
        Router.Evaluate();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Stops GG/Sonar (and optionally its autostart) and starts our router.</summary>
    public void TakeOverFromGG(bool disableAutostart)
    {
        SteelSeriesGG.Stop();
        if (disableAutostart)
            SteelSeriesGG.DisableAutostart();
        TryActivate();
    }

    public void SaveConfig(AppConfig config)
    {
        Config = config;
        config.Save();
        Router?.UpdateConfig(config);
    }

    /// <summary>
    /// The dial only reports ChatMix to the PC while the base station is in software (Sonar) mode;
    /// SteelSeries' engine toggles the same two flags when Sonar starts and stops.
    /// </summary>
    void EnableBaseStationSoftwareMode(bool enabled)
    {
        if (!Headset.IsConnected)
            return;
        try
        {
            Headset.SetSonarPresent(enabled);
            Headset.SetSoftwareChatMix(enabled);
        }
        catch (Exception)
        {
            // Not fatal: routing still works, only the dial won't report ChatMix.
        }
    }

    public void Dispose()
    {
        if (IsActive)
            EnableBaseStationSoftwareMode(false);
        Router?.Dispose();
        Sonar?.Dispose();
        Headset.Dispose();
        Endpoints.Dispose();
    }
}
