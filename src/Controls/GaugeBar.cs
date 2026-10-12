using System.Windows;
using System.Windows.Media;

namespace StressLab.Controls;

/// <summary>Rounded horizontal load gauge (0–100) with a configurable accent color.</summary>
public sealed class GaugeBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GaugeBar),
        new PropertyMetadata(0.0, (d, _) => ((GaugeBar)d).InvalidateVisual()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(GaugeBar),
        new PropertyMetadata(null, (d, _) => ((GaugeBar)d).InvalidateVisual()));

    private static readonly Brush TrackBrush = Frozen(0x1A, 0x21, 0x2B);
    private static readonly Brush DefaultAccent = Frozen(0x4D, 0xA3, 0xFF);

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public GaugeBar() => Height = 8;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        double r = h / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        double pct = Math.Clamp(Value, 0, 100) / 100.0;
        if (pct > 0.002)
            dc.DrawRoundedRectangle(Accent ?? DefaultAccent, null, new Rect(0, 0, Math.Max(h, w * pct), h), r, r);
    }
}
