using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AudioMixer.Core.Sonar;

/// <summary>A Sonar virtual channel. Each maps to one APO settings repository.</summary>
public enum Channel
{
    Game,
    Chat,
    Media,
    Aux,
    Mic,
}

/// <summary>
/// Drives the Sonar APO (the DSP/routing engine that runs inside the Windows audio engine) through
/// Sonar.APOAPI.dll. All COM calls run on one dedicated thread: the objects come straight from
/// DllGetClassObject, so there is no COM marshalling to make cross-thread calls safe.
/// </summary>
public sealed class SonarEngine : IDisposable
{
    static readonly Guid ClsidBasicControl = new("DEA05346-7BE3-4DB0-AE9F-14423648EA7B");
    static readonly Guid IidBasicControl = new("40076C40-0E9C-4D14-9CF6-34839A766B8A");
    static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");

    static readonly string[] CandidatePaths =
    [
        @"C:\Program Files\SteelSeries\GG\apps\sonar\Sonar.APOAPI.dll",
        @"C:\Program Files\SteelSeries\GG\apps\sonar\driver\apoDriverPackage\Sonar.APOAPI.dll",
    ];

    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;
    readonly Dictionary<Channel, IApoBasicControl> _controls = new();
    readonly Dictionary<Channel, IApoStore> _stores = new();
    readonly Dictionary<(Channel, ApoSetting), IApoSetting> _settings = new();
    IntPtr _library;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int DllGetClassObjectFn(in Guid rclsid, in Guid riid, out IntPtr ppv);

    public string LibraryPath { get; }

    SonarEngine(string libraryPath)
    {
        LibraryPath = libraryPath;
        _thread = new Thread(Pump) { IsBackground = true, Name = "SonarEngine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        Invoke(Open);
    }

    /// <summary>Finds the installed Sonar APO API and opens every channel repository.</summary>
    public static SonarEngine? TryCreate(out string? error)
    {
        var path = CandidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            error = "Sonar.APOAPI.dll not found (Sonar's virtual audio driver package is not installed).";
            return null;
        }
        try
        {
            error = null;
            return new SonarEngine(path);
        }
        catch (Exception e)
        {
            error = $"Failed to open Sonar APO API: {e.Message}";
            return null;
        }
    }

    static string RepositoryId(Channel channel) => channel switch
    {
        Channel.Game => "Game",
        Channel.Chat => "ChatRender",
        Channel.Media => "Media",
        Channel.Aux => "Aux",
        Channel.Mic => "ChatCapture",
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    void Open()
    {
        _library = NativeLibrary.Load(LibraryPath);
        var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectFn>(NativeLibrary.GetExport(_library, "DllGetClassObject"));
        foreach (var channel in Enum.GetValues<Channel>())
        {
            Marshal.ThrowExceptionForHR(getClassObject(ClsidBasicControl, IidClassFactory, out var factoryPtr));
            var factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);
            factory.CreateInstance(IntPtr.Zero, IidBasicControl, out var obj);
            var control = (IApoBasicControl)obj;
            control.Initialize(RepositoryId(channel));
            control.OpenStore(out var store);
            _controls[channel] = control;
            _stores[channel] = store;
        }
    }

    // ---- Routing ----

    /// <summary>Game/Media/Aux use the render redirection; Chat uses the "capture" redirection set (that is how the APO names it).</summary>
    static (ApoSetting State, ApoSetting Device, ApoSetting Gain, ApoSetting Mute) RedirectionSettings(Channel channel) => channel switch
    {
        Channel.Chat => (ApoSetting.CaptureStreamRedirectionState, ApoSetting.CaptureStreamRedirectionDeviceId,
                         ApoSetting.CaptureStreamRedirectionGainLin, ApoSetting.CaptureStreamRedirectionMute),
        Channel.Mic => throw new InvalidOperationException("The mic channel is not redirected by the APO; see MicBridge."),
        _ => (ApoSetting.StreamRedirectionState, ApoSetting.StreamRedirectionDeviceId,
              ApoSetting.StreamRedirectionGainLin, ApoSetting.StreamRedirectionMute),
    };

    public string GetOutputDevice(Channel channel) => GetString(channel, RedirectionSettings(channel).Device);

    /// <summary>Routes a channel to a physical render endpoint (MMDevice id), or disables the redirection when null/empty.</summary>
    public void SetOutputDevice(Channel channel, string? deviceId)
    {
        var s = RedirectionSettings(channel);
        Invoke(() =>
        {
            Setting(channel, s.Device).SetStringSetting(deviceId ?? "");
            Setting(channel, s.State).SetBoolSetting(!string.IsNullOrEmpty(deviceId));
        });
    }

    public float GetVolume(Channel channel) => GetFloat(channel, RedirectionSettings(channel).Gain);

    public void SetVolume(Channel channel, float linear) => SetFloat(channel, RedirectionSettings(channel).Gain, Math.Clamp(linear, 0f, 1f));

    public bool GetMute(Channel channel) => GetBool(channel, RedirectionSettings(channel).Mute);

    public void SetMute(Channel channel, bool muted) => SetBool(channel, RedirectionSettings(channel).Mute, muted);

    // ---- ChatMix ----

    /// <summary>
    /// Applies a ChatMix balance in [-1, 1]: negative favours game, positive favours chat, 0 = both full.
    /// Same curve as Sonar: s = sign(b)·2b/(b+sign(b)); other = 1−s, chat = 1+s (each clamped to [0,1]).
    /// Like Sonar, only Game is balanced against Chat. Media and Aux are held at full gain, which also clears any
    /// stale ChatMix gain left on them.
    /// </summary>
    public void SetChatMixBalance(float balance)
    {
        var (gameGain, chatGain) = ChatMixGains(balance);
        SetFloat(Channel.Game, ApoSetting.ChatMixGainLin, gameGain);
        SetFloat(Channel.Media, ApoSetting.ChatMixGainLin, 1f);
        SetFloat(Channel.Aux, ApoSetting.ChatMixGainLin, 1f);
        SetFloat(Channel.Chat, ApoSetting.CaptureChatMixGainLin, chatGain);
    }

    /// <summary>The linear gains a ChatMix balance gives Game and Chat (see <see cref="SetChatMixBalance"/>).</summary>
    public static (float Game, float Chat) ChatMixGains(float balance)
    {
        balance = Math.Clamp(balance, -1f, 1f);
        var scaled = Math.Abs(balance) < 0.001f ? 0f : Math.Sign(balance) * 2f * balance / (balance + Math.Sign(balance));
        return (Math.Clamp(1f - scaled, 0f, 1f), Math.Clamp(1f + scaled, 0f, 1f));
    }

    /// <summary>Converts the base station's dial report (game and chat level, 0–100, one of them 100) to a balance.</summary>
    public static float BalanceFromDial(int gamePercent, int chatPercent)
    {
        float game = Math.Clamp(gamePercent, 0, 100) / 100f, chat = Math.Clamp(chatPercent, 0, 100) / 100f;
        return game < 0.999f ? 1f - game : chat - 1f;
    }

    // ---- Generic access ----

    public bool GetBool(Channel channel, ApoSetting setting) => Invoke(() => { Setting(channel, setting).GetBoolSetting(out var v); return v; });
    public float GetFloat(Channel channel, ApoSetting setting) => Invoke(() => { Setting(channel, setting).GetFloatSetting(out var v); return v; });
    public int GetLong(Channel channel, ApoSetting setting) => Invoke(() => { Setting(channel, setting).GetLongSetting(out var v); return v; });
    public string GetString(Channel channel, ApoSetting setting) => Invoke(() => { Setting(channel, setting).GetStringSetting(out var v); return v ?? ""; });

    public void SetBool(Channel channel, ApoSetting setting, bool value) => Invoke(() => Setting(channel, setting).SetBoolSetting(value));
    public void SetFloat(Channel channel, ApoSetting setting, float value) => Invoke(() => Setting(channel, setting).SetFloatSetting(value));
    public void SetLong(Channel channel, ApoSetting setting, int value) => Invoke(() => Setting(channel, setting).SetLongSetting(value));
    public void SetString(Channel channel, ApoSetting setting, string value) => Invoke(() => Setting(channel, setting).SetStringSetting(value));

    public (float Default, float Min, float Max) GetFloatRange(Channel channel, ApoSetting setting) =>
        Invoke(() => { Setting(channel, setting).GetFloatTraits(out var d, out var min, out var max); return (d, min, max); });

    IApoSetting Setting(Channel channel, ApoSetting setting)
    {
        if (!_settings.TryGetValue((channel, setting), out var s))
        {
            _stores[channel].OpenSetting(setting, out s);
            _settings[(channel, setting)] = s;
        }
        return s;
    }

    // ---- Dispatcher ----

    void Pump()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
            action();
    }

    void Invoke(Action action) => Invoke<object?>(() => { action(); return null; });

    T Invoke<T>(Func<T> func)
    {
        if (Thread.CurrentThread == _thread)
            return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (_queue.IsAddingCompleted)
            return;
        Invoke(() =>
        {
            foreach (var s in _settings.Values) Marshal.ReleaseComObject(s);
            foreach (var s in _stores.Values) Marshal.ReleaseComObject(s);
            foreach (var c in _controls.Values) Marshal.ReleaseComObject(c);
            _settings.Clear();
            _stores.Clear();
            _controls.Clear();
        });
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}
