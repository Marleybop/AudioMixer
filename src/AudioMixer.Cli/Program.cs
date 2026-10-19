// Headless host for testing the engine against real hardware.
//   list    — audio endpoints and current Windows defaults
//   status  — base station / headset state and stored audio settings
//   init    — write a starting config from the devices present (headset first, controllers blocked)
//   run     — run the router until Ctrl+C, printing every decision and dial event
using AudioMixer.Core.Audio;
using AudioMixer.Core.Config;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Routing;
using AudioMixer.Core.Sonar;

var command = args.FirstOrDefault() ?? "list";
using var endpoints = new AudioEndpoints();

switch (command)
{
    case "list":
        List(endpoints);
        break;
    case "status":
        await Status();
        break;
    case "init":
        Init(endpoints);
        break;
    case "run":
        await Run(endpoints);
        break;
    case "hid":
        await Hid();
        break;
    case "enable" or "disable":
        PolicyConfig.SetEnabled(args[1], command == "enable");
        Thread.Sleep(500);
        Console.WriteLine($"{endpoints.Find(args[1])?.DisplayName}: {endpoints.Find(args[1])?.State}");
        break;
    case "reset-chatmix":
        using (var sonar = SonarEngine.TryCreate(out var error) ?? throw new InvalidOperationException(error))
        {
            sonar.SetChatMixBalance(0);
            foreach (var channel in new[] { Channel.Game, Channel.Media, Channel.Aux })
                Console.WriteLine($"{channel} ChatMix gain = {sonar.GetFloat(channel, ApoSetting.ChatMixGainLin)}");
        }
        break;
    case "watch":
        await Watch(args.Skip(1).FirstOrDefault());
        break;
    default:
        Console.WriteLine("usage: AudioMixer.Cli list|status|init|run");
        break;
}

static void List(AudioEndpoints endpoints)
{
    foreach (var e in endpoints.GetAll().OrderBy(e => e.Flow).ThenBy(e => e.State).ThenBy(e => e.DisplayName))
        Console.WriteLine($"{e.Flow,-6} {e.State,-10} {(e.SonarChannel?.ToString() ?? ""),-6} {e.DisplayName}\n       {e.Id}");
    Console.WriteLine();
    foreach (var flow in Enum.GetValues<EndpointFlow>())
        foreach (var role in Enum.GetValues<DeviceRole>())
        {
            var id = endpoints.GetDefaultId(flow, role);
            Console.WriteLine($"default {flow,-6} {role,-14} {endpoints.Find(id ?? "")?.DisplayName ?? id}");
        }
}

static async Task Status()
{
    using var headset = new NovaProWireless();
    headset.TryConnect();
    if (!headset.IsConnected)
    {
        Console.WriteLine("No Arctis Nova Pro Wireless base station found.");
        return;
    }
    var status = HeadsetStatus.FromWirelessSettings(await headset.QueryAsync(NovaProProtocol.WirelessSettings));
    Console.WriteLine(status);
    Console.WriteLine(await headset.GetAudioSettingsAsync() is var a
        ? $"volume={a.Volume} eqPreset={a.EqPreset} eq=[{string.Join(" ", a.CustomEqDb)}] mic={a.MicVolume} sidetone={a.Sidetone} chatmix={a.ChatMixGame}/{a.ChatMixChat}"
        : "");
}

static void Init(AudioEndpoints endpoints)
{
    var all = endpoints.GetAll().Where(e => !e.IsVirtual).ToList();
    static bool IsController(AudioEndpoint e) =>
        e.AdapterName.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
        || e.AdapterName.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
        || e.AdapterName.Contains("DualShock", StringComparison.OrdinalIgnoreCase);
    DeviceRef Ref(AudioEndpoint e) => new(e.Id, e.DisplayName);

    var config = new AppConfig
    {
        OutputPriority = all.Where(e => e.Flow == EndpointFlow.Output && !IsController(e))
            .OrderByDescending(e => e.IsNovaProWireless).ThenBy(e => e.State).Select(Ref).ToList(),
        InputPriority = all.Where(e => e.Flow == EndpointFlow.Input && !IsController(e))
            .OrderByDescending(e => e.IsNovaProWireless).ThenBy(e => e.State).Select(Ref).ToList(),
        Blocked = all.Where(IsController).Select(Ref).ToList(),
    };
    config.Save();
    Console.WriteLine($"Wrote {AppConfig.DefaultPath}");
    Console.WriteLine("Outputs:  " + string.Join(" > ", config.OutputPriority.Select(d => d.Name)));
    Console.WriteLine("Inputs:   " + string.Join(" > ", config.InputPriority.Select(d => d.Name)));
    Console.WriteLine("Blocked:  " + string.Join(", ", config.Blocked.Select(d => d.Name)));
}

static async Task Run(AudioEndpoints endpoints)
{
    var config = AppConfig.Load();
    using var headset = new NovaProWireless();
    using var sonar = SonarEngine.TryCreate(out var sonarError);
    Console.WriteLine(sonar is null ? $"Sonar: unavailable ({sonarError})" : $"Sonar: {sonar.LibraryPath}");

    headset.ConnectedChanged += (_, c) => Log($"base station {(c ? "connected" : "disconnected")}");
    headset.StatusChanged += (_, s) => Log($"headset {(s.IsHeadsetOn ? "ON" : "OFF")} battery={s.HeadsetBatteryPercent}% spare={s.SpareBatteryPercent}% {s.Charge} mic={(s.MicMuted ? "muted" : "live")} {s.NoiseControl}");
    headset.ChatMixChanged += (_, d) => Log($"dial chatmix game={d.Game} chat={d.Chat} → balance {SonarEngine.BalanceFromDial(d.Game, d.Chat):0.00}");

    using var router = new Router(endpoints, headset, sonar, config);
    router.StateChanged += (_, s) =>
    {
        Log($"route → output: {s.Output?.DisplayName ?? "(none)"} | mic: {s.Input?.DisplayName ?? "(none)"} | sonar={s.SonarActive} micBridge={s.MicBridgeRunning}");
        foreach (var p in s.Problems)
            Log($"  ! {p}");
    };
    headset.TryConnect();
    router.Evaluate();

    var done = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
    Log("running — Ctrl+C to stop");
    await done.Task;
}

static async Task Hid()
{
    foreach (var d in HidSharp.DeviceList.Local.GetHidDevices(NovaProProtocol.VendorId, NovaProProtocol.ProductId))
        Console.WriteLine($"{d.DevicePath}\n   in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feature={d.GetMaxFeatureReportLength()}");
    using var headset = new NovaProWireless();
    headset.ReportReceived += (_, r) => Log("rx " + Convert.ToHexString(r.AsSpan(0, Math.Min(20, r.Length))));
    headset.TryConnect();
    Log($"connected={headset.IsConnected}");
    try { headset.Send(NovaProProtocol.WirelessSettings); Log("sent 06 B0"); }
    catch (Exception e) { Log("send failed: " + e); }
    await Task.Delay(2000);
}

/// <summary>Logs every report except routine status replies. "chatmix-on" first enables software ChatMix (0x49 01).</summary>
static async Task Watch(string? option)
{
    using var headset = new NovaProWireless();
    headset.ReportReceived += (_, r) =>
    {
        if (r[1] != NovaProProtocol.WirelessSettings)
            Log("rx " + Convert.ToHexString(r.AsSpan(0, Math.Min(24, r.Length))));
    };
    headset.TryConnect();
    Log("ux_settings " + Convert.ToHexString((await headset.QueryAsync(NovaProProtocol.UxSettings)).AsSpan(0, 8)));
    if (option == "chatmix-on")
    {
        headset.SetSoftwareChatMix(true);
        Log("sent 06 49 01 (software ChatMix on)");
    }
    Log("watching 60 s — turn the ChatMix dial");
    await Task.Delay(TimeSpan.FromSeconds(60));
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
