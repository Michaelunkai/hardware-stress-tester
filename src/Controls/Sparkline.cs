using System.Windows;
using System.Windows.Media;
using StressLab.Engines;

namespace StressLab.Controls;

/// <summary>Lightweight dependency-free live graph (line + soft fill), 180-point history.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly RingBuffer _values = new(180);
    private readonly Pen _stroke = new(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)), 1.6);
    private readonly SolidColorBrush _fill = new(Color.FromArgb(0x30, 0x4D, 0xA3, 0xFF));
    private readonly Brush _baseline = Frozen(0x23, 0x2B, 0x36);
    private double _max = 1.0;

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public void SetColor(Color color)
    {
        ((SolidColorBrush)_stroke.Brush).Color = color;
        _fill.Color = Color.FromArgb(0x30, color.R, color.G, color.B);
        InvalidateVisual();
    }

    public void Push(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        _values.Add(value);
        if (value > _max) _max = value;
        InvalidateVisual();
    }

    public void Reset()
    {
        _values.Reset();
        _max = 1.0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;
        int n = _values.Count;
        dc.DrawLine(new Pen(_baseline, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (n < 2) return;

        double max = Math.Max(1e-9, _max);
        double Y(int i) => h - _values[i] / max * (h - 3) - 1.5;
        double X(int i) => w * i / (n - 1);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(0)), false, false);
            for (int i = 1; i < n; i++) ctx.LineTo(new Point(X(i), Y(i)), true, false);
        }
        line.Freeze();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(0)), true, false);
            for (int i = 1; i < n; i++) ctx.LineTo(new Point(X(i), Y(i)), true, false);
            ctx.LineTo(new Point(X(n - 1), h), true, false);
            ctx.LineTo(new Point(X(0), h), true, false);
        }
        area.Freeze();

        dc.DrawGeometry(_fill, null, area);
        dc.DrawGeometry(null, _stroke, line);
    }
}
