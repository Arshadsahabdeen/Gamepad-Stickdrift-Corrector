using System.Windows;
using System.Windows.Media;

namespace StickFix.App;

/// <summary>Draws a stick: hollow ring = raw from the controller, solid dot = what games get. Shape carries meaning, not only colour.</summary>
public sealed class StickView : FrameworkElement
{
    private const int TrailLength = 40;
    private readonly Queue<Point> _trail = new();
    private Point _raw, _output;
    private double _deadzone;
    private bool _showOutput;

    private static readonly Pen Outline = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), 1.5));
    private static readonly Pen Guide = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0x8A, 0x8A, 0x8A)), 1));
    private static readonly Pen RawPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0x8C, 0x28)), 2.5));
    private static readonly Pen TrailPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xF0, 0x8C, 0x28)), 1));
    private static readonly Pen ZonePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush OutputBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)));

    public void Update(double rawX, double rawY, double outX, double outY, double deadzone, bool showOutput)
    {
        var raw = new Point(rawX, rawY);
        if (raw == _raw && new Point(outX, outY) == _output && deadzone == _deadzone && showOutput == _showOutput) return;

        _raw = raw;
        _output = new Point(outX, outY);
        _deadzone = deadzone;
        _showOutput = showOutput;
        _trail.Enqueue(raw);
        while (_trail.Count > TrailLength) _trail.Dequeue();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size available)
    {
        var side = Math.Min(double.IsInfinity(available.Width) ? 170 : available.Width, 170);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 8;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        Point ToScreen(Point p) => new(center.X + p.X * radius, center.Y - p.Y * radius);

        dc.DrawEllipse(null, Outline, center, radius, radius);
        dc.DrawLine(Guide, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        dc.DrawLine(Guide, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
        if (_deadzone > 0) dc.DrawEllipse(null, ZonePen, center, _deadzone * radius, _deadzone * radius);

        Point? previous = null;
        foreach (var point in _trail)
        {
            if (previous is { } from) dc.DrawLine(TrailPen, ToScreen(from), ToScreen(point));
            previous = point;
        }

        if (_showOutput) dc.DrawEllipse(OutputBrush, null, ToScreen(_output), 6, 6);
        dc.DrawEllipse(null, RawPen, ToScreen(_raw), 8, 8);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
