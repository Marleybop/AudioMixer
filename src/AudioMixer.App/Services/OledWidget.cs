using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioMixer.Core.Headset;

namespace AudioMixer.App.Services;

/// <summary>What the base station's screen shows.</summary>
public enum ScreenMode
{
    /// <summary>The base station's own screen (what GG shows).</summary>
    BaseStation,
    /// <summary>AudioMixer's widget: battery, mic and volume.</summary>
    Widget,
    /// <summary>A picture or animated GIF from the Screens folder.</summary>
    Custom,
    /// <summary>A blank screen (the base station won't go below brightness 1).</summary>
    Off,
    /// <summary>CPU and GPU load and temperatures, and memory.</summary>
    Stats,
    /// <summary>The weather where you are.</summary>
    Weather,
    /// <summary>The time and date.</summary>
    Clock,
    /// <summary>Clock, widget, stats and weather in turn.</summary>
    Cycle,
}

/// <summary>The facts the widget shows.</summary>
public sealed record WidgetState(
    bool HeadsetOn,
    int HeadsetBattery,
    int SpareBattery,
    bool Charging,
    bool MicMuted,
    int Volume,
    int VolumeMax);

/// <summary>
/// Draws on the base station's OLED: AudioMixer's widget, PC stats, the weather, a clock, your own pictures and GIFs, or nothing. The base station
/// redraws its own screen when something happens on it (e.g. the volume dial), so the image is sent again every
/// second. The screen can also go to sleep (blank) after a while with no keyboard, mouse or dial use, and wakes
/// on the next one. Static images move by a pixel now and then so nothing burns into the OLED.
/// </summary>
public sealed partial class OledWidget
{
    static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Typeface Bold = new(TextFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface Semi = new(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Icons = new(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Where your own screens go.</summary>
    public static string ScreensFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioMixer", "Screens");

    public static readonly string[] ImageExtensions = [".png", ".gif", ".jpg", ".jpeg", ".bmp"];

    readonly NovaProWireless _headset;
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _animation;
    OledFrame? _last;
    WidgetState? _state;
    ScreenMode _mode = ScreenMode.BaseStation;
    List<(OledFrame Frame, TimeSpan Delay)> _custom = [];
    int _customIndex;
    bool _sending;
    bool _asleep;
    int _shift;
    DateTime _shiftedAt = DateTime.UtcNow;
    DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>The latest image (exactly the pixels the base station gets), for the preview in the app.</summary>
    public event EventHandler<BitmapSource>? Rendered;

    /// <summary>Raised when the screen goes to sleep or wakes up.</summary>
    public event EventHandler<bool>? SleepChanged;

    public OledWidget(NovaProWireless headset)
    {
        _headset = headset;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _animation = new DispatcherTimer();
        _animation.Tick += (_, _) => NextCustomFrame();
        _effectTimer.Tick += OnEffectTick;
        _timer.Start();
    }

    /// <summary>Blank the screen after this long without keyboard, mouse or dial use (null = never).</summary>
    public TimeSpan? SleepAfter { get; set; }

    public bool IsAsleep => _asleep;

    public ScreenMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            _last = null;
            RestartAnimation();
            if (value == ScreenMode.BaseStation && !_asleep)
                Try(_headset.ShowBaseStationScreen);
            Refresh(force: true);
            UpdateWeatherAnimation();
        }
    }

    int _loadVersion;

    /// <summary>
    /// Loads a picture or GIF for <see cref="ScreenMode.Custom"/> on a background thread (long GIFs take a moment to
    /// decode), then plays it from the first frame. <paramref name="onError"/> gets a message if it can't be read.
    /// </summary>
    public void LoadCustom(string? path, Action<string>? onError = null)
    {
        var version = ++_loadVersion;
        _custom = [];
        _last = null;
        RestartAnimation();
        Refresh(force: true);
        if (path is null)
            return;
        var ui = Dispatcher.CurrentDispatcher;
        var thread = new Thread(() =>
        {
            try
            {
                var frames = LoadImage(path);
                ui.BeginInvoke(() =>
                {
                    if (version != _loadVersion)
                        return; // something else was picked meanwhile
                    _custom = frames;
                    _last = null;
                    RestartAnimation();
                    Refresh(force: true);
                });
            }
            catch (Exception e)
            {
                ui.BeginInvoke(() => onError?.Invoke($"Couldn't read {Path.GetFileName(path)}: {e.Message}"));
            }
        }) { IsBackground = true, Name = "Screen image loader" };
        thread.SetApartmentState(ApartmentState.STA); // WPF imaging needs STA
        thread.Start();
    }

    public void Update(WidgetState state)
    {
        if (state == _state)
            return;
        _state = state;
        Refresh(force: false);
    }

    /// <summary>Something happened on the base station (dial, mute): counts as activity and wakes the screen.</summary>
    public void NotifyActivity()
    {
        _lastActivity = DateTime.UtcNow;
        if (_asleep)
            Tick();
    }

    /// <summary>Hands the screen back to the base station (when AudioMixer quits).</summary>
    public void Release()
    {
        _timer.Stop();
        _animation.Stop();
        _effectTimer.Stop();
        if (_mode != ScreenMode.BaseStation || _asleep)
            Try(_headset.ShowBaseStationScreen);
    }

    void Tick()
    {
        var idle = DateTime.UtcNow - Max(_lastActivity, LastInputTime());
        var asleep = SleepAfter is { } after && idle >= after;
        if (asleep != _asleep)
        {
            _asleep = asleep;
            _last = null;
            RestartAnimation();
            SleepChanged?.Invoke(this, asleep);
            if (!asleep && _mode == ScreenMode.BaseStation)
                Try(_headset.ShowBaseStationScreen);
        }
        if (_mode != ScreenMode.BaseStation || _asleep)
            Refresh(force: true);
        // On the clock, tick just after each second starts so the seconds don't lag or skip.
        _timer.Interval = CurrentPage() == ScreenMode.Clock && !_asleep
            ? TimeSpan.FromMilliseconds(1015 - DateTime.Now.Millisecond)
            : TimeSpan.FromSeconds(1);
        UpdateWeatherAnimation(); // the page may have changed (Cycle) or the screen gone to sleep
    }

    /// <summary>Plays a GIF from its first frame (when it's picked, when the screen switches to it, or after sleep).</summary>
    void RestartAnimation()
    {
        _customIndex = 0;
        _animation.Stop();
        UpdateAnimation();
    }

    void UpdateAnimation()
    {
        var animate = _mode == ScreenMode.Custom && !_asleep && _custom.Count > 1;
        if (animate && !_animation.IsEnabled)
        {
            _animation.Interval = _custom[_customIndex].Delay;
            _animation.Start();
        }
        else if (!animate)
        {
            _animation.Stop();
        }
    }

    void NextCustomFrame()
    {
        if (_custom.Count == 0)
            return;
        _customIndex = (_customIndex + 1) % _custom.Count;
        _animation.Interval = _custom[_customIndex].Delay;
        Refresh(force: true);
    }

    void Refresh(bool force)
    {
        // Move still pictures by a pixel every few minutes so no pixel stays lit for hours.
        if (DateTime.UtcNow - _shiftedAt > TimeSpan.FromMinutes(3))
        {
            _shift = (_shift + 1) % 4;
            _shiftedAt = DateTime.UtcNow;
        }
        var frame = CurrentFrame();
        Rendered?.Invoke(this, Preview(frame));
        var drawing = _asleep || _mode != ScreenMode.BaseStation;
        if (!drawing || !_headset.IsConnected || ScreenBorrowed())
            return;
        if (!force && frame.SameAs(_last))
            return;
        _last = frame;
        if (_sending)
            return; // the next tick sends the latest
        _sending = true;
        Task.Run(() =>
        {
            try { _headset.DrawOled(frame); }
            catch (Exception) { }
            finally { _sending = false; }
        });
    }

    OledFrame CurrentFrame()
    {
        if (_asleep || _mode == ScreenMode.Off)
            return new OledFrame();
        if (_mode == ScreenMode.Custom)
        {
            if (_custom.Count == 0)
                return new OledFrame();
            var frame = _custom[_customIndex].Frame;
            return _custom.Count == 1 ? Shifted(frame) : frame;
        }
        var page = CurrentPage();
        if (page == ScreenMode.Weather)
            return WeatherFrame();
        return ToFrame(Render(page switch
        {
            ScreenMode.Stats => DrawStats,
            ScreenMode.Clock => DrawClock,
            _ => dc => { if (_state is { } s) DrawWidget(dc, s); },
        })); // the widget is also previewed while the base station shows its own screen
    }

    OledFrame Shifted(OledFrame frame)
    {
        int dx = _shift is 1 or 2 ? 1 : 0, dy = _shift is 2 or 3 ? 1 : 0;
        if (dx == 0 && dy == 0)
            return frame;
        var moved = new OledFrame();
        for (var y = 0; y < OledFrame.Height; y++)
            for (var x = 0; x < OledFrame.Width; x++)
                moved[x, y] = frame[x - dx, y - dy];
        return moved;
    }

    /// <summary>
    /// Another tool can borrow the screen by holding a named mutex "AudioMixer.OledBusy" in the same session;
    /// AudioMixer stops drawing until it's released.
    /// </summary>
    static bool ScreenBorrowed()
    {
        if (!Mutex.TryOpenExisting("AudioMixer.OledBusy", out var mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    // ---- The widget ----

    /// <summary>Draws a page in white on black, moved by the burn-in shift.</summary>
    BitmapSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, OledFrame.Width, OledFrame.Height));
            dc.PushTransform(new TranslateTransform(_shift is 1 or 2 ? 1 : 0, _shift is 2 or 3 ? 1 : 0));
            draw(dc);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(OledFrame.Width, OledFrame.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static void DrawWidget(DrawingContext dc, WidgetState s)
    {
        if (!s.HeadsetOn)
        {
            Text(dc, "Headset off", Bold, 15, 2, 6);
            Text(dc, $"Spare battery {s.SpareBattery}%", Semi, 11, 2, 30);
            return;
        }

        // Row 1: headset battery (big) and the spare (small).
        Battery(dc, 1, 3, 26, 13, s.HeadsetBattery);
        Text(dc, $"{s.HeadsetBattery}%", Bold, 18, 32, -2);
        if (s.Charging)
            Text(dc, "", Icons, 11, 74, 4); // lightning bolt
        Text(dc, "SPARE", Semi, 8, 96, 0);
        Text(dc, $"{s.SpareBattery}%", Semi, 11, 96, 8);

        // Row 2: the mic, as large as it can be.
        if (s.MicMuted)
        {
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 24, 127, 22), 4, 4);
            Text(dc, "", Icons, 14, 6, 28, Brushes.Black);
            Text(dc, "MIC MUTED", Bold, 15, 26, 25, Brushes.Black);
        }
        else
        {
            dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1), new Rect(0.5, 24.5, 126, 21), 4, 4);
            Text(dc, "", Icons, 14, 6, 28);
            Text(dc, "MIC LIVE", Bold, 15, 26, 25);
        }

        // Row 3: volume.
        Text(dc, "", Icons, 10, 1, 52);
        var width = 104.0;
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(17.5, 54.5, width, 6));
        var fill = Math.Clamp(s.Volume / (double)Math.Max(1, s.VolumeMax), 0, 1) * (width - 2);
        if (fill >= 1)
            dc.DrawRectangle(Brushes.White, null, new Rect(19, 56, fill, 3));
    }

    static void Battery(DrawingContext dc, double x, double y, double w, double h, int percent)
    {
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(x + 0.5, y + 0.5, w - 3, h - 1));
        dc.DrawRectangle(Brushes.White, null, new Rect(x + w - 2, y + h / 2 - 2.5, 2, 5));
        var fill = Math.Clamp(percent, 0, 100) / 100.0 * (w - 7);
        if (fill >= 1)
            dc.DrawRectangle(Brushes.White, null, new Rect(x + 2, y + 2, fill, h - 4));
    }

    static void Text(DrawingContext dc, string text, Typeface face, double size, double x, double y, Brush? brush = null) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush ?? Brushes.White, 1.0),
            new Point(x, y));

    // ---- Your own pictures ----

    /// <summary>
    /// Loads a picture (one frame) or an animated GIF (every frame, composed as the GIF says, with its delays),
    /// fitted to 128×64 and turned into black and white: bright pixels are lit.
    /// </summary>
    static List<(OledFrame, TimeSpan)> LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frames = new List<(OledFrame, TimeSpan)>();
        int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
        if (decoder.Metadata is BitmapMetadata meta && decoder is GifBitmapDecoder)
        {
            width = Read(meta, "/logscrdesc/Width", width);
            height = Read(meta, "/logscrdesc/Height", height);
        }
        var canvas = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        foreach (var source in decoder.Frames.Take(2000))
        {
            var delay = TimeSpan.FromMilliseconds(100);
            int left = 0, top = 0, disposal = 0;
            if (source.Metadata is BitmapMetadata m && decoder is GifBitmapDecoder)
            {
                delay = TimeSpan.FromMilliseconds(Math.Max(Read(m, "/grctlext/Delay", 10), 2) * 10);
                left = Read(m, "/imgdesc/Left", 0);
                top = Read(m, "/imgdesc/Top", 0);
                disposal = Read(m, "/grctlext/Disposal", 0);
            }
            var layer = new DrawingVisual();
            using (var dc = layer.RenderOpen())
                dc.DrawImage(source, new Rect(left, top, source.PixelWidth, source.PixelHeight));
            canvas.Render(layer);
            frames.Add((ToFrame(Fit(canvas)), delay < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay));
            if (disposal == 2)
                canvas.Clear(); // "restore to background" (close enough for small GIFs)
        }
        return frames;
    }

    static T Read<T>(BitmapMetadata meta, string query, T fallback)
    {
        try { return meta.GetQuery(query) is { } v ? (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture) : fallback; }
        catch (Exception) { return fallback; }
    }

    /// <summary>Scales a picture to fit 128×64 (keeping its shape), centred on black.</summary>
    static BitmapSource Fit(BitmapSource source)
    {
        var scale = Math.Min((double)OledFrame.Width / source.PixelWidth, (double)OledFrame.Height / source.PixelHeight);
        double w = source.PixelWidth * scale, h = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, OledFrame.Width, OledFrame.Height));
            dc.DrawImage(source, new Rect((OledFrame.Width - w) / 2, (OledFrame.Height - h) / 2, w, h));
        }
        var bitmap = new RenderTargetBitmap(OledFrame.Width, OledFrame.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    static BitmapSource Preview(OledFrame frame)
    {
        var pixels = new byte[OledFrame.Width * OledFrame.Height];
        for (var y = 0; y < OledFrame.Height; y++)
            for (var x = 0; x < OledFrame.Width; x++)
                pixels[y * OledFrame.Width + x] = frame[x, y] ? (byte)255 : (byte)0;
        var bitmap = BitmapSource.Create(OledFrame.Width, OledFrame.Height, 96, 96, PixelFormats.Gray8, null, pixels, OledFrame.Width);
        bitmap.Freeze();
        return bitmap;
    }

    static OledFrame ToFrame(BitmapSource bitmap)
    {
        var stride = OledFrame.Width * 4;
        var bgra = new byte[stride * OledFrame.Height];
        bitmap.CopyPixels(bgra, stride, 0);
        var gray = new byte[OledFrame.Width * OledFrame.Height];
        for (var i = 0; i < gray.Length; i++)
            gray[i] = (byte)((bgra[i * 4] * 29 + bgra[i * 4 + 1] * 150 + bgra[i * 4 + 2] * 77) >> 8);
        return OledFrame.FromGray(gray, OledFrame.Width);
    }

    // ---- Idle time ----

    static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>When the keyboard or mouse was last used on this PC (any app).</summary>
    static DateTime LastInputTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            return DateTime.UtcNow;
        var idleMs = unchecked((uint)Environment.TickCount - info.Time);
        return DateTime.UtcNow - TimeSpan.FromMilliseconds(idleMs);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInputInfo info);

    static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { }
    }
}
