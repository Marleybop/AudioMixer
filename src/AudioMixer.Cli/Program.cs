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
    case "dim-timer" or "sleep-timer":
        // dim-timer|sleep-timer <minutes>: write it, then read back what the base station stored
        using (var headset = new NovaProWireless())
        {
            headset.TryConnect();
            var minutes = int.Parse(args[1]);
            if (command == "dim-timer") headset.SetDimTimer(minutes);
            else headset.SetSleepTimer(minutes);
            await Task.Delay(300);
            Console.WriteLine(command == "dim-timer"
                ? $"dim after {(await headset.GetUxSettingsAsync()).DimAfterMinutes} min"
                : $"sleep after {HeadsetStatus.FromWirelessSettings(await headset.QueryAsync(NovaProProtocol.WirelessSettings)).SleepAfterMinutes} min");
        }
        break;
    case "mute-light":
        // mute-light <1-10>: set the mic-muted light brightness and store it in the base station
        using (var headset = new NovaProWireless())
        {
            headset.TryConnect();
            headset.SetMutedMicBrightness(int.Parse(args[1]));
            headset.SaveToFlash();
            await Task.Delay(300);
            Console.WriteLine($"mute light = {HeadsetStatus.FromWirelessSettings(await headset.QueryAsync(NovaProProtocol.WirelessSettings)).MutedMicBrightness}");
        }
        break;
    case "oled-test":
        // oled-test: draw a border and a cross on the base station's screen for 3 s, then give the screen back
        using (var headset = new NovaProWireless())
        {
            headset.TryConnect();
            var frame = new OledFrame();
            for (var x = 0; x < OledFrame.Width; x++) { frame[x, 0] = frame[x, OledFrame.Height - 1] = true; frame[x, x / 2] = frame[x, OledFrame.Height - 1 - x / 2] = true; }
            for (var y = 0; y < OledFrame.Height; y++) frame[0, y] = frame[OledFrame.Width - 1, y] = true;
            headset.DrawOled(frame);
            Console.WriteLine("drawn");
            await Task.Delay(3000);
            headset.ShowBaseStationScreen();
            Console.WriteLine("restored");
        }
        break;
    case "driver-restore":
        // driver-restore <backup folder>: reinstall Sonar's virtual audio driver from exported packages (run as administrator)
        Console.WriteLine(SonarDriverInstaller.Install(args[1]));
        break;
    case "driver-status":
        Console.WriteLine(SonarEngine.TryCreate(out var engineError) is { } probe
            ? $"Sonar engine API: {probe.LibraryPath}"
            : $"Sonar engine API: {engineError}");
        foreach (var e in endpoints.GetAll().Where(e => e.SonarChannel is not null || e.DisplayName.Contains("Sonar")))
            Console.WriteLine($"{e.Flow,-6} {e.State,-10} {e.DisplayName}");
        break;
    case "presets":
        foreach (var group in SonarPresets.Load().GroupBy(p => p.Channel))
            Console.WriteLine($"{group.Key}: {group.Count()} presets, selected: {string.Join(", ", group.Where(p => p.IsSelectedInSonar).Select(p => p.Name))}\n  "
                + string.Join(", ", group.Take(12).Select(p => p.Name)));
        break;
    case "sound":
        // sound <channel>: the channel's EQ/effects as read from the APO, and a check that writing them back changes nothing
        using (var sonar = SonarEngine.TryCreate(out var error) ?? throw new InvalidOperationException(error))
        {
            var channel = Enum.Parse<Channel>(args[1], ignoreCase: true);
            var sound = SoundApo.FromApo(sonar, channel);
            Console.WriteLine(sound.ToJson());
            var roundTrip = SoundSettings.FromJson(sound.ToJson());
            foreach (var (setting, value) in SoundApo.ToApo(channel, roundTrip))
            {
                object current = value switch
                {
                    bool => sonar.GetBool(channel, setting),
                    int => sonar.GetLong(channel, setting),
                    _ => sonar.GetFloat(channel, setting),
                };
                if (!Equals(current, value) && !(value is float f && Math.Abs(f - (float)current) < 1e-4))
                    Console.WriteLine($"  differs: {setting} apo={current} ours={value}");
            }
        }
        break;
    case "apo":
        // apo <Game|Chat|Media|Aux|Mic> [name filter]: every APO setting of that channel, with type and range
        using (var sonar = SonarEngine.TryCreate(out var error) ?? throw new InvalidOperationException(error))
        {
            var channel = Enum.Parse<Channel>(args[1], ignoreCase: true);
            var filter = args.Skip(2).FirstOrDefault() ?? "";
            foreach (var setting in Enum.GetValues<ApoSetting>().Where(s => s != ApoSetting.NumSettings))
            {
                if (!setting.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                string text;
                try { text = sonar.Describe(channel, setting); }
                catch (Exception e) { text = $"error {e.HResult:X8}"; }
                Console.WriteLine($"{setting,-46} {text}");
            }
        }
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
