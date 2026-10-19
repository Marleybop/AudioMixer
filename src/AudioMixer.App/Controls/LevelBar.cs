using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioMixer.App.Controls;

/// <summary>
/// A bar you set by clicking or dragging anywhere along it (no thumb to aim for), with the mouse wheel or
/// the arrow keys. It draws a fill from <see cref="Origin"/> to <see cref="Value"/>, so it also works as a
/// balance or ±dB bar, and with <see cref="Segments"/> it draws stepped blocks (battery, brightness,
/// level meters). With <see cref="IsReadOnly"/> it is a display only.
/// </summary>
public class LevelBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(LevelBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinimumProperty = Register(nameof(Minimum), 0.0);
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 100.0);
    /// <summary>Where the fill starts; NaN = <see cref="Minimum"/>. Set 0 on a −x…+x range for a centred bar.</summary>
    public static readonly DependencyProperty OriginProperty = Register(nameof(Origin), double.NaN);
    /// <summary>Values snap to multiples of this (0 = no snapping).</summary>
    public static readonly DependencyProperty StepProperty = Register(nameof(Step), 0.0);
    /// <summary>Change per wheel notch or arrow key.</summary>
    public static readonly DependencyProperty SmallChangeProperty = Register(nameof(SmallChange), 1.0);
    /// <summary>Value restored by a double-click; NaN = double-click does nothing.</summary>
    public static readonly DependencyProperty ResetValueProperty = Register(nameof(ResetValue), double.NaN);
    public static readonly DependencyProperty OrientationProperty = Register(nameof(Orientation), Orientation.Vertical);
    /// <summary>Number of blocks to draw (0 = one continuous bar).</summary>
    public static readonly DependencyProperty SegmentsProperty = Register(nameof(Segments), 0);
    public static readonly DependencyProperty SegmentGapProperty = Register(nameof(SegmentGap), 2.0);
    public static readonly DependencyProperty FillProperty = Register<Brush?>(nameof(Fill), Brushes.Orange);
    /// <summary>Fill for blocks above <see cref="HotFraction"/> of the bar (meters: the clipping zone).</summary>
    public static readonly DependencyProperty HotFillProperty = Register<Brush?>(nameof(HotFill), null);
    public static readonly DependencyProperty HotFractionProperty = Register(nameof(HotFraction), 0.9);
    public static readonly DependencyProperty TrackProperty = Register<Brush?>(nameof(Track), Brushes.Black);
    /// <summary>Thin line drawn at the value (continuous bars only); null = none.</summary>
    public static readonly DependencyProperty CapProperty = Register<Brush?>(nameof(Cap), null);
    public static readonly DependencyProperty CornerRadiusProperty = Register(nameof(CornerRadius), 3.0);
    public static readonly DependencyProperty IsReadOnlyProperty = Register(nameof(IsReadOnly), false);
    /// <summary>Slider look: thickness of a slim track drawn across the middle (0 = fill the whole element).</summary>
    public static readonly DependencyProperty TrackThicknessProperty = Register(nameof(TrackThickness), 0.0);
    /// <summary>Diameter of the round handle drawn at the value (0 = none).</summary>
    public static readonly DependencyProperty ThumbSizeProperty = Register(nameof(ThumbSize), 0.0);
    public static readonly DependencyProperty ThumbProperty = Register<Brush?>(nameof(Thumb), Brushes.White);

    static DependencyProperty Register<T>(string name, T defaultValue) =>
        DependencyProperty.Register(name, typeof(T), typeof(LevelBar), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    static LevelBar()
    {
        FocusableProperty.OverrideMetadata(typeof(LevelBar), new FrameworkPropertyMetadata(true));
        CursorProperty.OverrideMetadata(typeof(LevelBar), new FrameworkPropertyMetadata(Cursors.Hand));
        FocusVisualStyleProperty.OverrideMetadata(typeof(LevelBar), new FrameworkPropertyMetadata(null));
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Origin { get => (double)GetValue(OriginProperty); set => SetValue(OriginProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public double SmallChange { get => (double)GetValue(SmallChangeProperty); set => SetValue(SmallChangeProperty, value); }
    public double ResetValue { get => (double)GetValue(ResetValueProperty); set => SetValue(ResetValueProperty, value); }
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public int Segments { get => (int)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    public double SegmentGap { get => (double)GetValue(SegmentGapProperty); set => SetValue(SegmentGapProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? HotFill { get => (Brush?)GetValue(HotFillProperty); set => SetValue(HotFillProperty, value); }
    public double HotFraction { get => (double)GetValue(HotFractionProperty); set => SetValue(HotFractionProperty, value); }
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush? Cap { get => (Brush?)GetValue(CapProperty); set => SetValue(CapProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public double TrackThickness { get => (double)GetValue(TrackThicknessProperty); set => SetValue(TrackThicknessProperty, value); }
    public double ThumbSize { get => (double)GetValue(ThumbSizeProperty); set => SetValue(ThumbSizeProperty, value); }
    public Brush? Thumb { get => (Brush?)GetValue(ThumbProperty); set => SetValue(ThumbProperty, value); }

    bool Vertical => Orientation == Orientation.Vertical;
    double Range => Math.Max(Maximum - Minimum, double.Epsilon);
    double Fraction(double v) => Math.Clamp((v - Minimum) / Range, 0, 1);
    double OriginValue => double.IsNaN(Origin) ? Minimum : Origin;

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        var lo = Math.Min(Fraction(OriginValue), Fraction(Value));
        var hi = Math.Max(Fraction(OriginValue), Fraction(Value));

        if (Segments > 0)
        {
            var length = Vertical ? h : w;
            var size = (length - SegmentGap * (Segments - 1)) / Segments;
            for (var i = 0; i < Segments; i++)
            {
                var start = i * (size + SegmentGap);
                var mid = (i + 0.5) / Segments;
                var lit = mid >= lo && mid <= hi && hi > lo;
                var brush = !lit ? Track : HotFill is not null && mid > HotFraction ? HotFill : Fill;
                var rect = Vertical ? new Rect(0, h - start - size, w, size) : new Rect(start, 0, size, h);
                dc.DrawRoundedRectangle(brush, null, rect, 1, 1);
            }
            DrawFocus(dc, w, h);
            return;
        }

        if (TrackThickness > 0)
        {
            DrawSlider(dc, w, h, lo, hi);
            return;
        }

        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), CornerRadius, CornerRadius);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), CornerRadius, CornerRadius));
        dc.DrawRectangle(Fill, null, Vertical ? new Rect(0, h * (1 - hi), w, h * (hi - lo)) : new Rect(w * lo, 0, w * (hi - lo), h));
        if (Cap is not null)
        {
            var f = Fraction(Value);
            dc.DrawRectangle(Cap, null, Vertical ? new Rect(0, Math.Clamp(h * (1 - f) - 1, 0, h - 2), w, 2) : new Rect(Math.Clamp(w * f - 1, 0, w - 2), 0, 2, h));
        }
        dc.Pop();
        DrawFocus(dc, w, h);
    }

    /// <summary>A slim track with the filled part, an optional hot (clipping) part and a round handle.</summary>
    void DrawSlider(DrawingContext dc, double w, double h, double lo, double hi)
    {
        var t = Math.Min(TrackThickness, Vertical ? w : h);
        var inset = ThumbSize / 2; // keep the handle inside the element at both ends
        var length = Math.Max((Vertical ? h : w) - 2 * inset, 1);
        Rect Span(double a, double b) => Vertical
            ? new Rect((w - t) / 2, inset + length * (1 - b), t, length * (b - a))
            : new Rect(inset + length * a, (h - t) / 2, length * (b - a), t);

        dc.DrawRoundedRectangle(Track, null, Span(0, 1), t / 2, t / 2);
        if (hi > lo)
        {
            if (HotFill is not null && hi > HotFraction)
            {
                dc.DrawRoundedRectangle(Fill, null, Span(lo, Math.Min(hi, HotFraction)), t / 2, t / 2);
                dc.DrawRoundedRectangle(HotFill, null, Span(Math.Max(lo, HotFraction), hi), t / 2, t / 2);
            }
            else
            {
                dc.DrawRoundedRectangle(Fill, null, Span(lo, hi), t / 2, t / 2);
            }
        }
        if (ThumbSize > 0)
        {
            var f = Fraction(Value);
            var c = Vertical ? new Point(w / 2, inset + length * (1 - f)) : new Point(inset + length * f, h / 2);
            dc.DrawEllipse(Thumb, new Pen(Fill, 2), c, ThumbSize / 2, ThumbSize / 2);
            if (IsKeyboardFocused)
                dc.DrawEllipse(null, new Pen(Fill, 1.5), c, ThumbSize / 2 + 4, ThumbSize / 2 + 4);
        }
    }

    void DrawFocus(DrawingContext dc, double w, double h)
    {
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Fill, 1.5), new Rect(-2, -2, w + 4, h + 4), CornerRadius + 2, CornerRadius + 2);
    }

    // ---- Input ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (IsReadOnly)
            return;
        Focus();
        if (e.ClickCount == 2 && !double.IsNaN(ResetValue))
        {
            SetClamped(ResetValue);
            e.Handled = true;
            return;
        }
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured)
            SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // In a page that scrolls, the wheel scrolls the page unless you've clicked into this bar first;
        // otherwise scrolling past a bar would quietly change its setting.
        if (IsReadOnly || (!IsKeyboardFocused && InScrollingPage()))
            return;
        SetClamped(Value + Math.Sign(e.Delta) * SmallChange);
        e.Handled = true;
    }

    bool InScrollingPage()
    {
        for (DependencyObject? d = VisualTreeHelper.GetParent(this); d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ScrollViewer { ScrollableHeight: > 0 })
                return true;
        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsReadOnly)
            return;
        var delta = e.Key switch
        {
            Key.Up or Key.Right => SmallChange,
            Key.Down or Key.Left => -SmallChange,
            Key.PageUp => SmallChange * 10,
            Key.PageDown => -SmallChange * 10,
            _ => 0.0,
        };
        if (delta != 0)
            SetClamped(Value + delta);
        else if (e.Key == Key.Home)
            SetClamped(Minimum);
        else if (e.Key == Key.End)
            SetClamped(Maximum);
        else
            return;
        e.Handled = true;
    }

    void SetFromPoint(Point p)
    {
        var inset = TrackThickness > 0 ? ThumbSize / 2 : 0;
        var f = Vertical
            ? 1 - (p.Y - inset) / Math.Max(ActualHeight - 2 * inset, 1)
            : (p.X - inset) / Math.Max(ActualWidth - 2 * inset, 1);
        SetClamped(Minimum + Math.Clamp(f, 0, 1) * Range);
    }

    internal void SetClamped(double v)
    {
        if (Step > 0)
            v = Math.Round(v / Step) * Step;
        v = Math.Clamp(v, Minimum, Maximum);
        if (v != Value)
            SetCurrentValue(ValueProperty, v);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override AutomationPeer OnCreateAutomationPeer() => new LevelBarAutomationPeer(this);
}

/// <summary>Exposes the bar to screen readers as a range control.</summary>
sealed class LevelBarAutomationPeer(LevelBar owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
{
    protected override AutomationControlType GetAutomationControlTypeCore() =>
        owner.IsReadOnly ? AutomationControlType.ProgressBar : AutomationControlType.Slider;

    public override object? GetPattern(PatternInterface pattern) =>
        pattern == PatternInterface.RangeValue ? this : base.GetPattern(pattern);

    public double Value => owner.Value;
    public bool IsReadOnly => owner.IsReadOnly;
    public double Maximum => owner.Maximum;
    public double Minimum => owner.Minimum;
    public double LargeChange => owner.SmallChange * 10;
    public double SmallChange => owner.SmallChange;
    public void SetValue(double value) => owner.SetClamped(value);
}
