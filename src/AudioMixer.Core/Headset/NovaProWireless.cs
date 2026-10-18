using System.Collections.Concurrent;
using HidSharp;

namespace AudioMixer.Core.Headset;

/// <summary>
/// Live connection to an Arctis Nova Pro Wireless base station. Opens the command interface (MI_04 Col01)
/// and the sync interface (MI_04 Col02), answers queries, raises events for state the base station pushes,
/// and reconnects automatically when the base station is unplugged and plugged back in.
/// Shared HID access: works alongside other software that has the device open.
/// </summary>
public sealed class NovaProWireless : IDisposable
{
    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(1);
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan MinCommandGap = TimeSpan.FromMilliseconds(50); // the device can't take commands faster

    readonly object _writeLock = new();
    readonly ConcurrentDictionary<byte, ConcurrentBag<TaskCompletionSource<byte[]>>> _pending = new();
    readonly CancellationTokenSource _cts = new();
    HidStream? _command;
    HidStream? _sync;
    DateTime _lastWrite;
    HeadsetStatus? _status;

    public event EventHandler<bool>? ConnectedChanged;
    public event EventHandler<HeadsetStatus>? StatusChanged;
    /// <summary>The ChatMix dial moved: (game level, chat level), 0–100, one of them is 100.</summary>
    public event EventHandler<(int Game, int Chat)>? ChatMixChanged;
    /// <summary>The base station volume dial moved (0–56).</summary>
    public event EventHandler<int>? VolumeChanged;
    /// <summary>Any report from the device (for diagnostics).</summary>
    public event EventHandler<byte[]>? ReportReceived;

    public bool IsConnected => _command is not null;
    public HeadsetStatus? Status => _status;

    public NovaProWireless()
    {
        DeviceList.Local.Changed += (_, _) => TryConnect();
        _ = Task.Run(PollLoop);
    }

    /// <summary>Connects if a base station is present. Safe to call repeatedly.</summary>
    public void TryConnect()
    {
        lock (_writeLock)
        {
            if (_command is not null)
                return;
            var devices = DeviceList.Local.GetHidDevices(NovaProProtocol.VendorId, NovaProProtocol.ProductId).ToList();
            var command = devices.FirstOrDefault(d => d.DevicePath.Contains("mi_04&col01", StringComparison.OrdinalIgnoreCase));
            var sync = devices.FirstOrDefault(d => d.DevicePath.Contains("mi_04&col02", StringComparison.OrdinalIgnoreCase));
            if (command is null || !command.TryOpen(out var commandStream))
                return;
            commandStream.ReadTimeout = Timeout.Infinite;
            _command = commandStream;
            StartReader(commandStream);
            if (sync is not null && sync.TryOpen(out var syncStream))
            {
                syncStream.ReadTimeout = Timeout.Infinite;
                _sync = syncStream;
                StartReader(syncStream);
            }
        }
        ConnectedChanged?.Invoke(this, true);
        _ = Task.Run(RefreshStatus);
    }

    void StartReader(HidStream stream)
    {
        var thread = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "NovaPro HID reader" };
        thread.Start();
    }

    void ReadLoop(HidStream stream)
    {
        var buffer = new byte[stream.Device.GetMaxInputReportLength()];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var n = stream.Read(buffer, 0, buffer.Length);
                if (n >= 2 && buffer[0] is NovaProProtocol.ReportId or NovaProProtocol.SyncReportId)
                    HandleReport(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (Exception) when (!_cts.IsCancellationRequested)
        {
            Disconnect();
        }
        catch (Exception)
        {
        }
    }

    void HandleReport(byte[] report)
    {
        ReportReceived?.Invoke(this, report);
        var command = report[1];
        if (report[0] == NovaProProtocol.ReportId && _pending.TryRemove(command, out var waiters))
            foreach (var waiter in waiters)
                waiter.TrySetResult(report);

        switch (command)
        {
            case NovaProProtocol.WirelessSettings:
                UpdateStatus(HeadsetStatus.FromWirelessSettings(report));
                break;
            case NovaProProtocol.ConnectionEvent:
            case NovaProProtocol.BatteryStatus:
                // Partial pushes: re-read the full picture rather than patching fields.
                _ = Task.Run(RefreshStatus);
                break;
            case NovaProProtocol.ChatMixEvent:
                ChatMixChanged?.Invoke(this, (report[2], report[3]));
                break;
            case NovaProProtocol.VolumeEvent when report[0] == NovaProProtocol.SyncReportId:
                VolumeChanged?.Invoke(this, report[2]);
                break;
        }
    }

    void UpdateStatus(HeadsetStatus status)
    {
        if (status == _status)
            return;
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    async Task RefreshStatus()
    {
        try { await QueryAsync(NovaProProtocol.WirelessSettings); }
        catch (Exception) { }
    }

    async Task PollLoop()
    {
        // Backstop for missed pushes and for picking up a base station that appears later.
        while (!_cts.IsCancellationRequested)
        {
            TryConnect();
            if (IsConnected)
                await RefreshStatus();
            try { await Task.Delay(PollInterval, _cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Sends a read command and returns the device's reply report.</summary>
    public async Task<byte[]> QueryAsync(byte command)
    {
        // Several callers may query the same command at once; one reply answers all of them.
        var waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.GetOrAdd(command, _ => []).Add(waiter);
        Send(command);
        var done = await Task.WhenAny(waiter.Task, Task.Delay(QueryTimeout));
        if (done != waiter.Task)
        {
            waiter.TrySetCanceled();
            throw new TimeoutException($"No reply to 0x{command:X2}");
        }
        return await waiter.Task;
    }

    public async Task<BaseAudioSettings> GetAudioSettingsAsync() =>
        BaseAudioSettings.FromReport(await QueryAsync(NovaProProtocol.AudioSettings));

    /// <summary>Sends a command with optional parameter bytes. Refuses anything not on the allowlist.</summary>
    public void Send(byte command, params byte[] parameters)
    {
        if (!NovaProProtocol.AllowedCommands.Contains(command))
            throw new InvalidOperationException($"Command 0x{command:X2} is not allowed.");
        try
        {
            lock (_writeLock)
            {
                var stream = _command ?? throw new InvalidOperationException("Base station not connected.");
                var report = new byte[stream.Device.GetMaxOutputReportLength()];
                report[0] = NovaProProtocol.ReportId;
                report[1] = command;
                parameters.CopyTo(report, 2);
                var wait = _lastWrite + MinCommandGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    Thread.Sleep(wait);
                stream.Write(report);
                _lastWrite = DateTime.UtcNow;
            }
        }
        catch (IOException)
        {
            Disconnect();
            throw;
        }
    }

    // ---- Typed settings ----

    public void SetCustomEq(IReadOnlyList<double> bandsDb)
    {
        if (bandsDb.Count != 10)
            throw new ArgumentException("The base station EQ has exactly 10 bands.", nameof(bandsDb));
        Send(NovaProProtocol.EqPreset, 4); // 4 = Custom
        Send(NovaProProtocol.CustomEq, bandsDb.Select(NovaProProtocol.EqGainToByte).ToArray());
    }

    public void SetSidetone(int level) => Send(NovaProProtocol.Sidetone, (byte)Math.Clamp(level, 0, 3));
    public void SetMicVolume(int level) => Send(NovaProProtocol.MicVolume, (byte)Math.Clamp(level, 1, 10));
    public void SetHighGain(bool enabled) => Send(NovaProProtocol.HighGain, (byte)(enabled ? 2 : 1));
    public void SetOledBrightness(int level) => Send(NovaProProtocol.OledBrightness, (byte)Math.Clamp(level, 1, 10));
    /// <summary>Tells the base station whether Sonar-style software (us) is managing ChatMix/routing.</summary>
    public void SetSonarPresent(bool present) => Send(NovaProProtocol.SonarPresent, (byte)(present ? 1 : 0));
    public void SetSoftwareChatMix(bool enabled) => Send(NovaProProtocol.SoftwareChatMix, (byte)(enabled ? 1 : 0));

    // ---- Lifecycle ----

    void Disconnect()
    {
        bool wasConnected;
        lock (_writeLock)
        {
            wasConnected = _command is not null;
            DisconnectLocked();
        }
        if (wasConnected)
            ConnectedChanged?.Invoke(this, false);
    }

    void DisconnectLocked()
    {
        _command?.Dispose();
        _sync?.Dispose();
        _command = null;
        _sync = null;
        _status = null;
        foreach (var waiter in _pending.Values.SelectMany(w => w))
            waiter.TrySetCanceled();
        _pending.Clear();
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_writeLock)
            DisconnectLocked();
    }
}
