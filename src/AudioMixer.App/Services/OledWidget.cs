using System.Globalization;
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
    /// <summary>A blank screen (the base station won't go below brightness 1).</summary>
    Off,
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
/// Draws AudioMixer's widget (or a blank screen) on the base station's OLED. The base station redraws its own
/// screen when something happens on it (e.g. the volume dial), so the image is sent again every second, and at
/// once when anything shown changes. Everything moves by a pixel now and then so nothing burns into the OLED.
/// </summary>
public sealed class OledWidget
{
    static readonly FontFamily TextFont = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly Typeface Bold = new(TextFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    static readonly Typeface Semi = new(TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Icons = new(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    readonly NovaProWireless _headset;
    readonly DispatcherTimer _timer;
    OledFrame? _last;
    WidgetState? _state;
    ScreenMode _mode = ScreenMode.BaseStation;
    bool _sending;
    int _shift;
    DateTime _shiftedAt = DateTime.UtcNow;

    /// <summary>The latest image (exactly the pixels the base station gets), for the preview on the Headset page.</summary>
    public event EventHandler<BitmapSource>? Rendered;

    public OledWidget(NovaProWireless headset)
    {
        _headset = headset;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh(force: true);
    }

    public ScreenMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            _last = null;
            if (value == ScreenMode.BaseStation)
            {
                _timer.Stop();
                Try(_headset.ShowBaseStationScreen);
            }
            else
            {
                _timer.Start();
            }
            Refresh(force: true);
        }
    }

    public void Update(WidgetState state)
    {
        if (state == _state)
            return;
        _state = state;
        Refresh(force: false);
    }

    /// <summary>Redraws now (the volume dial makes the base station show its own screen for a moment).</summary>
    public void Redraw() => Refresh(force: true);

    /// <summary>Hands the screen back to the base station (when AudioMixer quits).</summary>
    public void Release()
    {
        _timer.Stop();
        if (_mode != ScreenMode.BaseStation)
            Try(_headset.ShowBaseStationScreen);
    }

    void Refresh(bool force)
    {
        // Move everything by a pixel every few minutes so no pixel stays lit for hours.
        if (DateTime.UtcNow - _shiftedAt > TimeSpan.FromMinutes(3))
        {
            _shift = (_shift + 1) % 4;
            _shiftedAt = DateTime.UtcNow;
        }
        var frame = ToFrame(Render());
        Rendered?.Invoke(this, Preview(frame));
        if (_mode == ScreenMode.BaseStation || !_headset.IsConnected || ScreenBorrowed())
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

        BitmapSource Render()
    {
        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, OledFrame.Width, OledFrame.Height));
            if (_mode != ScreenMode.Off && _state is { } s)
            {
                var dx = _shift is 1 or 2 ? 1 : 0;
                var dy = _shift is 2 or 3 ? 1 : 0;
                dc.PushTransform(new TranslateTransform(dx, dy));
                DrawWidget(dc, s);
                dc.Pop();
            }
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
            gray[i] = bgra[i * 4 + 1]; // green channel; the image is black and white anyway
        return OledFrame.FromGray(gray, OledFrame.Width);
    }

    static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { }
    }
}
