using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AudioMixer.Core.Headset;
using AudioMixer.Core.Stats;
using AudioMixer.Core.Weather;

namespace AudioMixer.App.Services;

/// <summary>The stats and weather pages, and the cycle between them and the widget.</summary>
public sealed partial class OledWidget
{
    /// <summary>How long each page shows in <see cref="ScreenMode.Cycle"/>.</summary>
    public static readonly TimeSpan CycleTime = TimeSpan.FromSeconds(10);

    PcStats? _stats;
    WeatherReport? _weather;
    string? _weatherPlace;
    string? _weatherNote;
    readonly WeatherEffect _effect = new();
    readonly DispatcherTimer _effectTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
    readonly Stopwatch _effectClock = new();
    (WeatherReport?, string?, string?, int) _weatherBaseKey;
    OledFrame? _weatherBase;
    bool _animateWeather = true;

    /// <summary>Animate the weather page: snow, rain, fog, lightning, stars.</summary>
    public bool AnimateWeather
    {
        get => _animateWeather;
        set
        {
            _animateWeather = value;
            UpdateWeatherAnimation();
            Refresh(force: true);
        }
    }

    public void UpdateStats(PcStats stats)
    {
        _stats = stats;
        if (CurrentPage() == ScreenMode.Stats)
            Refresh(force: false);
    }

    /// <summary>The weather to show; <paramref name="note"/> is shown instead when there's no report (e.g. "Pick a town").</summary>
    public void UpdateWeather(WeatherReport? report, string? place, string? note)
    {
        (_weather, _weatherPlace, _weatherNote) = (report, place, note);
        if (report is not null)
            _effect.Set(report.Code, report.IsDay, report.Wind, heavy: report.Code is 65 or 67 or 75 or 82 or 86 or 99);
        UpdateWeatherAnimation();
        if (CurrentPage() == ScreenMode.Weather)
            Refresh(force: false);
    }

    /// <summary>Runs the weather animation only while the weather page is on screen and awake.</summary>
    void UpdateWeatherAnimation()
    {
        var run = _animateWeather && !_asleep && _weather is not null && _effect.Animates && CurrentPage() == ScreenMode.Weather;
        if (run == _effectTimer.IsEnabled)
            return;
        if (run)
        {
            _effectClock.Restart();
            _effectTimer.Start();
        }
        else
        {
            _effectTimer.Stop();
        }
    }

    void OnEffectTick(object? sender, EventArgs e)
    {
        _effect.Step(Math.Min(_effectClock.Elapsed.TotalSeconds, 0.25));
        _effectClock.Restart();
        Refresh(force: true);
    }

    /// <summary>The weather page; the still part is only redrawn when it changes.</summary>
    OledFrame WeatherFrame()
    {
        var key = (_weather, _weatherPlace, _weatherNote, _shift);
        if (_weatherBase is null || key != _weatherBaseKey)
        {
            _weatherBase = ToFrame(Render(DrawWeather));
            _weatherBaseKey = key;
        }
        return _effectTimer.IsEnabled ? _effect.Apply(_weatherBase) : _weatherBase;
    }

    /// <summary>Which page is on screen now: in Cycle mode it changes every <see cref="CycleTime"/>.</summary>
    ScreenMode CurrentPage()
    {
        if (_mode != ScreenMode.Cycle)
            return _mode;
        ScreenMode[] pages = _weatherPlace is null ? [ScreenMode.Widget, ScreenMode.Stats] : [ScreenMode.Widget, ScreenMode.Stats, ScreenMode.Weather];
        var slot = (long)(DateTime.UtcNow.Ticks / CycleTime.Ticks);
        return pages[(int)(slot % pages.Length)];
    }

    // ---- Stats ----

    void DrawStats(DrawingContext dc)
    {
        if (_stats is not { } s)
        {
            Text(dc, "Reading stats…", Semi, 11, 2, 24);
            return;
        }
        StatRow(dc, 0, "CPU", s.CpuLoad, s.CpuTemp);
        StatRow(dc, 22, "GPU", s.GpuLoad, s.GpuTemp);

        // Bottom row: memory.
        var ram = $"RAM {s.RamUsedGb:0.0}/{s.RamTotalGb:0}G";
        var vram = s.VramUsedGb is { } used && s.VramTotalGb is { } total ? $"VRAM {used:0.0}/{total:0}G" : "";
        var size = 10.0;
        while (size > 7 && Width(ram, Semi, size) + Width(vram, Semi, size) > 122)
            size -= 0.5;
        Text(dc, ram, Semi, size, 0, 49);
        Text(dc, vram, Semi, size, 128 - Width(vram, Semi, size), 49);
    }

    /// <summary>One line: name, load and temperature, with a full-width load bar under it.</summary>
    static void StatRow(DrawingContext dc, double y, string name, double? load, double? temp)
    {
        Text(dc, name, Bold, 10, 0, y + 3);
        Text(dc, load is { } l ? $"{l:0}%" : "–", Bold, 16, 27, y - 2);
        if (temp is { } t)
        {
            var text = $"{t:0}°C";
            Text(dc, text, Bold, 16, 128 - Width(text, Bold, 16), y - 2);
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(0.5, y + 17.5, 126, 3));
        var fill = Math.Clamp(load ?? 0, 0, 100) / 100 * 125;
        if (fill >= 1)
            dc.DrawRectangle(Brushes.White, null, new Rect(1, y + 18, fill, 2));
    }

    // ---- Weather ----

    void DrawWeather(DrawingContext dc)
    {
        if (_weather is not { } w)
        {
            Text(dc, _weatherPlace ?? "Weather", Bold, 13, 0, 4);
            Text(dc, _weatherNote ?? "Getting the weather…", Semi, 10, 0, 30);
            return;
        }
        WeatherIcon(dc, w.Code, w.IsDay);

        var temp = Degrees(w.Temperature);
        Text(dc, temp, Bold, 26, 40, -7);
        var right = 40 + Width(temp, Bold, 26) + 4;
        var feels = $"feels {Degrees(w.FeelsLike)}";
        var extra = w.RainChance is >= 20 and var rain ? $"{(w.Code is >= 71 and <= 77 or 85 or 86 ? "snow" : "rain")} {rain}%"
            : $"wind {w.Wind:0}";
        if (right + Width(feels, Semi, 9) <= 128)
        {
            Text(dc, feels, Semi, 9, 128 - Width(feels, Semi, 9), 1);
            Text(dc, extra, Semi, 9, 128 - Width(extra, Semi, 9), 13);
        }

        Text(dc, w.Description, Semi, 11, 0, 32);
        var range = $"H {Degrees(w.High)}  L {Degrees(w.Low)}";
        Text(dc, range, Semi, 10, 0, 49);
        if (_weatherPlace is { } place && Width(range, Semi, 10) + Width(place, Semi, 10) < 124)
            Text(dc, place, Semi, 10, 128 - Width(place, Semi, 10), 49);
    }

    /// <summary>"−4°": rounded, with a real minus sign and no "−0".</summary>
    static string Degrees(double value)
    {
        var rounded = (int)Math.Round(value);
        return (rounded < 0 ? "−" + (-rounded).ToString(CultureInfo.InvariantCulture) : rounded.ToString(CultureInfo.InvariantCulture)) + "°";
    }

    /// <summary>A 36×30 picture of the WMO weather code, top left.</summary>
    static void WeatherIcon(DrawingContext dc, int code, bool day)
    {
        var white = Brushes.White;
        var cut = new Pen(Brushes.Black, 2); // separates overlapping shapes
        var thin = new Pen(white, 1.4);
        switch (code)
        {
            case 0 or 1:
                if (day) Sun(dc, 18, 15, 7);
                else Moon(dc, 18, 15);
                if (code == 1)
                    dc.DrawGeometry(white, cut, Cloud(17, 20, 0.55));
                return;
            case 2:
                if (day) Sun(dc, 13, 10, 6);
                else Moon(dc, 13, 10);
                dc.DrawGeometry(white, cut, Cloud(20, 18, 0.8));
                return;
            case 3:
                dc.DrawGeometry(null, thin, Cloud(13, 9, 0.6));
                dc.DrawGeometry(white, cut, Cloud(20, 18, 0.85));
                return;
            case 45 or 48:
                for (var i = 0; i < 4; i++)
                    dc.DrawRectangle(white, null, new Rect(2 + (i % 2) * 4, 5 + i * 6, 28, 2));
                return;
        }
        if (code is >= 95)
        {
            dc.DrawGeometry(white, null, Cloud(18, 8, 0.75));
            dc.DrawGeometry(white, cut, Geometry.Parse("M 20,13 L 12,24 L 18,24 L 14,32 L 26,19 L 20,19 L 24,13 Z"));
            return;
        }
        dc.DrawGeometry(white, null, Cloud(18, 11, 0.85));
        var snow = code is >= 71 and <= 77 or 85 or 86;
        for (var i = 0; i < 4; i++)
        {
            double x = 7 + i * 7, y = 22 + (i % 2) * 4;
            if (snow)
            {
                dc.DrawRectangle(white, null, new Rect(x - 1.5, y - 0.5, 3, 1));
                dc.DrawRectangle(white, null, new Rect(x - 0.5, y - 1.5, 1, 3));
            }
            else
            {
                dc.DrawLine(thin, new Point(x + 1, y - 2), new Point(x - 1, y + 3));
            }
        }
    }

    static void Sun(DrawingContext dc, double cx, double cy, double r)
    {
        dc.DrawEllipse(Brushes.White, null, new Point(cx, cy), r, r);
        var pen = new Pen(Brushes.White, 1.5);
        for (var k = 0; k < 8; k++)
        {
            var a = k * Math.PI / 4;
            dc.DrawLine(pen, new Point(cx + Math.Cos(a) * (r + 3), cy + Math.Sin(a) * (r + 3)),
                new Point(cx + Math.Cos(a) * (r + 6), cy + Math.Sin(a) * (r + 6)));
        }
    }

    static void Moon(DrawingContext dc, double cx, double cy) =>
        dc.DrawGeometry(Brushes.White, null, new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), 10, 10), new EllipseGeometry(new Point(cx + 6, cy - 4), 9, 9)));

    /// <summary>A cloud centred on (cx, cy), about 34×20 at scale 1.</summary>
    static Geometry Cloud(double cx, double cy, double scale)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(cx - 8 * scale, cy + 2 * scale), 8 * scale, 7 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 1 * scale, cy - 3 * scale), 10 * scale, 10 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 10 * scale, cy + 3 * scale), 7 * scale, 6 * scale));
        g.Children.Add(new RectangleGeometry(new Rect(cx - 8 * scale, cy + 2 * scale, 18 * scale, 7 * scale)));
        return new CombinedGeometry(GeometryCombineMode.Union, g, Geometry.Empty);
    }

    static double Width(string text, Typeface face, double size) =>
        new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, Brushes.White, 1.0).WidthIncludingTrailingWhitespace;
}
