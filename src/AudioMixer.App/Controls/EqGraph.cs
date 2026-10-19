using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AudioMixer.Core.Sonar;

namespace AudioMixer.App.Controls;

/// <summary>One EQ band as the graph sees it.</summary>
public interface IEqBand : INotifyPropertyChanged
{
    int Number { get; }
    bool Enabled { get; }
    FilterType Type { get; }
    double Frequency { get; set; }
    double Gain { get; set; }
    double Q { get; set; }
}

/// <summary>
/// The frequency response of a 10-band parametric EQ, drawn from the same biquad maths the APO uses.
/// Drag a band's point to move its frequency and gain, use the mouse wheel on it to change its width (Q),
/// double-click to flatten it. Arrow keys move the selected band.
/// </summary>
public class EqGraph : FrameworkElement
{
    public const double MinFreq = 20, MaxFreq = 20000, MaxGain = 12, MinQ = 0.5, MaxQ = 10;
    const double SampleRate = 48000;
    const double PadLeft = 34, PadBottom = 20, PadTop = 10, PadRight = 10;
    const double HitRadius = 12;

    public static readonly DependencyProperty BandsProperty = DependencyProperty.Register(nameof(Bands), typeof(IEnumerable), typeof(EqGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((EqGraph)d).OnBandsChanged(e)));
    public static readonly DependencyProperty SelectedBandProperty = DependencyProperty.Register(nameof(SelectedBand), typeof(IEqBand), typeof(EqGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Extra fixed filters drawn into the total (Sonar's bass / voice / treble boosts).</summary>
    public static readonly DependencyProperty ExtraFiltersProperty = DependencyProperty.Register(nameof(ExtraFilters), typeof(IReadOnlyList<EqFilter>), typeof(EqGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(EqGraph),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(EqGraph),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Bands { get => (IEnumerable?)GetValue(BandsProperty); set => SetValue(BandsProperty, value); }
    public IEqBand? SelectedBand { get => (IEqBand?)GetValue(SelectedBandProperty); set => SetValue(SelectedBandProperty, value); }
    public IReadOnlyList<EqFilter>? ExtraFilters { get => (IReadOnlyList<EqFilter>?)GetValue(ExtraFiltersProperty); set => SetValue(ExtraFiltersProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    static readonly Brush GridBrush = Frozen(Color.FromRgb(0x26, 0x28, 0x2D));
    static readonly Brush ZeroBrush = Frozen(Color.FromRgb(0x3A, 0x3D, 0x44));
    static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x8A, 0x87, 0x7F));
    static readonly Brush BandCurveBrush = Frozen(Color.FromArgb(0x55, 0xA9, 0xA6, 0x9F));
    static readonly Brush NodeFill = Frozen(Color.FromRgb(0x14, 0x15, 0x18));
    static readonly Typeface LabelFace = new("Segoe UI Variable Text, Segoe UI");

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static EqGraph()
    {
        FocusableProperty.OverrideMetadata(typeof(EqGraph), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(EqGraph), new FrameworkPropertyMetadata(null));
        ClipToBoundsProperty.OverrideMetadata(typeof(EqGraph), new FrameworkPropertyMetadata(true));
    }

    readonly List<IEqBand> _bands = [];
    IEqBand? _dragging;

    void OnBandsChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += OnCollectionChanged;
        Resubscribe();
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Resubscribe();

    void Resubscribe()
    {
        foreach (var b in _bands)
            b.PropertyChanged -= OnBandChanged;
        _bands.Clear();
        if (Bands is not null)
            _bands.AddRange(Bands.OfType<IEqBand>());
        foreach (var b in _bands)
            b.PropertyChanged += OnBandChanged;
        InvalidateVisual();
    }

    void OnBandChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    // ---- Geometry ----

    Rect Plot => new(PadLeft, PadTop, Math.Max(1, ActualWidth - PadLeft - PadRight), Math.Max(1, ActualHeight - PadTop - PadBottom));

    double X(double freq) { var p = Plot; return p.Left + Math.Log(freq / MinFreq) / Math.Log(MaxFreq / MinFreq) * p.Width; }
    double Y(double db) { var p = Plot; return p.Top + (1 - (Math.Clamp(db, -MaxGain, MaxGain) + MaxGain) / (2 * MaxGain)) * p.Height; }
    double FreqAt(double x) { var p = Plot; return MinFreq * Math.Pow(MaxFreq / MinFreq, Math.Clamp((x - p.Left) / p.Width, 0, 1)); }
    double GainAt(double y) { var p = Plot; return (1 - Math.Clamp((y - p.Top) / p.Height, 0, 1)) * 2 * MaxGain - MaxGain; }

    /// <summary>Pass and notch filters have no gain: their point sits on the 0 dB line.</summary>
    static bool HasGain(FilterType t) => t is FilterType.Peaking or FilterType.LowShelf or FilterType.HighShelf;

    Point NodePoint(IEqBand b) => new(X(b.Frequency), Y(HasGain(b.Type) ? b.Gain : 0));

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable everywhere
        var gridPen = new Pen(GridBrush, 1);
        foreach (var f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
        {
            var x = Math.Round(X(f)) + 0.5;
            dc.DrawLine(gridPen, new Point(x, p.Top), new Point(x, p.Bottom));
            Label(dc, f >= 1000 ? $"{f / 1000:0}k" : $"{f:0}", x, p.Bottom + 4, center: true);
        }
        for (var db = -12; db <= 12; db += 6)
        {
            var y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(db == 0 ? new Pen(ZeroBrush, 1) : gridPen, new Point(p.Left, y), new Point(p.Right, y));
            Label(dc, db > 0 ? $"+{db}" : $"{db}", p.Left - 6, y - 7, center: false);
        }

        var enabled = _bands.Where(b => b.Enabled).ToList();
        var filters = enabled.Select(b => new EqFilter(true, (float)b.Frequency, b.Type, (float)b.Gain, (float)b.Q)).ToList();
        if (ExtraFilters is { } extra)
            filters.AddRange(extra.Where(f => f.Enabled));

        const int points = 220;
        var freqs = Enumerable.Range(0, points).Select(i => MinFreq * Math.Pow(MaxFreq / MinFreq, i / (points - 1.0))).ToArray();

        // Each band on its own, faintly; the selected one in the accent.
        var bandPen = new Pen(BandCurveBrush, 1);
        foreach (var b in enabled)
        {
            var single = new EqFilter(true, (float)b.Frequency, b.Type, (float)b.Gain, (float)b.Q);
            var pen = b == SelectedBand ? new Pen(Accent, 1) { DashStyle = DashStyles.Dash } : bandPen;
            dc.DrawGeometry(null, pen, Curve(freqs, f => Response(single, f)));
        }

        // The total.
        var total = freqs.Select(f => filters.Sum(x => Response(x, f))).ToArray();
        var line = Curve(freqs, f => 0, total);
        var fill = new StreamGeometry();
        using (var ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(X(freqs[0]), Y(0)), true, true);
            for (var i = 0; i < points; i++)
                ctx.LineTo(new Point(X(freqs[i]), Y(total[i])), true, false);
            ctx.LineTo(new Point(X(freqs[^1]), Y(0)), true, false);
        }
        fill.Freeze();
        var accent = IsActive ? Accent : LabelBrush;
        var fillBrush = accent.Clone();
        fillBrush.Opacity = 0.16;
        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, new Pen(accent, 2.2) { LineJoin = PenLineJoin.Round }, line);

        // Points.
        foreach (var b in _bands)
        {
            var pt = NodePoint(b);
            var selected = b == SelectedBand;
            var r = selected ? 10 : 8;
            var stroke = !b.Enabled ? ZeroBrush : selected ? Accent : (Brush)LabelBrush;
            dc.DrawEllipse(selected ? accent : NodeFill, new Pen(stroke, 1.6), pt, r, r);
            var text = Text(b.Number.ToString(CultureInfo.InvariantCulture), selected ? Brushes.Black : b.Enabled ? Brushes.White : LabelBrush, 10);
            dc.DrawText(text, new Point(pt.X - text.Width / 2, pt.Y - text.Height / 2));
        }

        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Accent, 1), new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1), 4, 4);
    }

    StreamGeometry Curve(double[] freqs, Func<double, double> db, double[]? values = null)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (var i = 0; i < freqs.Length; i++)
            {
                var pt = new Point(X(freqs[i]), Y(values?[i] ?? db(freqs[i])));
                if (i == 0) ctx.BeginFigure(pt, false, false);
                else ctx.LineTo(pt, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    void Label(DrawingContext dc, string s, double x, double y, bool center)
    {
        var t = Text(s, LabelBrush, 10);
        dc.DrawText(t, new Point(center ? x - t.Width / 2 : x - t.Width, y));
    }

    FormattedText Text(string s, Brush brush, double size) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Gain in dB of one biquad at a frequency (RBJ cookbook formulas, as used by the APO's filters).</summary>
    public static double Response(EqFilter f, double freq)
    {
        if (!f.Enabled || f.Type is FilterType.Bypass or FilterType.AllPass)
            return 0;
        var a = Math.Pow(10, f.Gain / 40.0);
        var w0 = 2 * Math.PI * Math.Clamp(f.Frequency, 10, SampleRate / 2 - 1) / SampleRate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * Math.Max(f.QFactor, 0.05));
        var sa = 2 * Math.Sqrt(a) * alpha;
        double b0, b1, b2, a0, a1, a2;
        switch (f.Type)
        {
            case FilterType.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = b0; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = b0; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.BandPass:
                b0 = Math.Sin(w0) / 2; b1 = 0; b2 = -b0; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.BandPass0dB:
                b0 = alpha; b1 = 0; b2 = -alpha; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1; a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha; break;
            case FilterType.LowShelf:
                b0 = a * ((a + 1) - (a - 1) * cos + sa); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - sa);
                a0 = (a + 1) + (a - 1) * cos + sa; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - sa; break;
            case FilterType.HighShelf:
                b0 = a * ((a + 1) + (a - 1) * cos + sa); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sa);
                a0 = (a + 1) - (a - 1) * cos + sa; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sa; break;
            default: // peaking
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a; a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a; break;
        }
        var w = 2 * Math.PI * freq / SampleRate;
        double cw = Math.Cos(w), sw = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = b0 + b1 * cw + b2 * c2, ni = -(b1 * sw + b2 * s2);
        double dr = a0 + a1 * cw + a2 * c2, di = -(a1 * sw + a2 * s2);
        var mag2 = (nr * nr + ni * ni) / Math.Max(dr * dr + di * di, 1e-30);
        return 10 * Math.Log10(Math.Max(mag2, 1e-12));
    }

    // ---- Input ----

    IEqBand? HitTestBand(Point p)
    {
        // Selected first, then enabled bands, then the rest, so overlapping points stay reachable.
        var order = _bands.OrderByDescending(b => b == SelectedBand).ThenByDescending(b => b.Enabled);
        return order.FirstOrDefault(b => (NodePoint(b) - p).Length <= HitRadius);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var band = HitTestBand(e.GetPosition(this));
        if (band is null)
            return;
        SelectedBand = band;
        if (e.ClickCount == 2)
        {
            band.Gain = 0;
        }
        else if (band.Enabled)
        {
            _dragging = band;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging is { } band && IsMouseCaptured)
        {
            band.Frequency = Math.Round(FreqAt(p.X));
            if (HasGain(band.Type))
                band.Gain = Math.Round(GainAt(p.Y) * 10) / 10;
            return;
        }
        Cursor = HitTestBand(p) is { Enabled: true } ? Cursors.SizeAll : HitTestBand(p) is not null ? Cursors.Hand : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = null;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Over a point, or anywhere once the graph has been clicked.
        var band = HitTestBand(e.GetPosition(this)) ?? (IsKeyboardFocused ? SelectedBand : null);
        if (band is null || !band.Enabled)
            return;
        SelectedBand = band;
        band.Q = Math.Round(Math.Clamp(band.Q * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinQ, MaxQ), 2);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (SelectedBand is not { Enabled: true } band)
            return;
        switch (e.Key)
        {
            case Key.Left: band.Frequency = Math.Round(Math.Max(MinFreq, band.Frequency / 1.05)); break;
            case Key.Right: band.Frequency = Math.Round(Math.Min(MaxFreq, band.Frequency * 1.05)); break;
            case Key.Up: band.Gain = Math.Min(MaxGain, Math.Round(band.Gain + 0.5, 1)); break;
            case Key.Down: band.Gain = Math.Max(-MaxGain, Math.Round(band.Gain - 0.5, 1)); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
}
