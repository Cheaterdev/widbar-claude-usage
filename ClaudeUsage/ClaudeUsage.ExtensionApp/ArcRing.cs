using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace ClaudeUsage.ExtensionApp;

// A thick circular progress arc with round caps on a dim track, like the
// Pomodoro Focus widget. 0% starts at 12 o'clock and runs clockwise.
public sealed class ArcRing : Grid
{
    // Base colours per ring, plus warning colours the arc shifts to as usage climbs.
    public static readonly Color Coral = Color.FromArgb(255, 0xFF, 0x5A, 0x42);
    public static readonly Color Blue = Color.FromArgb(255, 0x35, 0x8C, 0xFF);
    // Hotter variants for the thin taskbar rings: a 2.5 px stroke has few fully
    // coloured pixels, so the same colour reads duller than on the big flyout rings.
    public static readonly Color CoralHot = Color.FromArgb(255, 0xFF, 0x42, 0x24);
    public static readonly Color BlueHot = Color.FromArgb(255, 0x14, 0x78, 0xFF);
    public static readonly Color TealHot = Color.FromArgb(255, 0x00, 0xEB, 0x9C);
    public static readonly Color Teal = Color.FromArgb(255, 0x12, 0xE0, 0xA4);
    public static readonly Color Amber = Color.FromArgb(255, 0xFF, 0xB0, 0x1F);
    public static readonly Color Red = Color.FromArgb(255, 0xFF, 0x36, 0x45);

    private readonly double _size;
    private readonly double _thickness;
    private readonly Ellipse _track;
    private readonly Microsoft.UI.Xaml.Shapes.Path _arc = new();
    private readonly Ellipse _full;

    public ArcRing(double size, double thickness)
    {
        _size = size;
        _thickness = thickness;
        Width = size;
        Height = size;

        _track = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
        };
        _full = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            Visibility = Visibility.Collapsed,
        };
        _arc.Width = size;
        _arc.Height = size;
        _arc.HorizontalAlignment = HorizontalAlignment.Left;
        _arc.VerticalAlignment = VerticalAlignment.Top;
        _arc.StrokeThickness = thickness;
        _arc.StrokeStartLineCap = PenLineCap.Round;
        _arc.StrokeEndLineCap = PenLineCap.Round;

        Children.Add(_track);
        Children.Add(_arc);
        Children.Add(_full);
    }

    // Usage at or above CritAt turns red, at or above WarnAt amber, otherwise the ring's own colour.
    public static double WarnAt = 80;
    public static double CritAt = 95;

    public static Color Warn(Color baseColor, double pct) =>
        pct >= CritAt ? Red : pct >= WarnAt ? Amber : baseColor;

    public void SetValue(double pct, Color baseColor)
    {
        var color = Warn(baseColor, pct);
        var brush = new SolidColorBrush(color);
        _arc.Stroke = brush;
        _full.Stroke = brush;

        pct = Math.Clamp(double.IsNaN(pct) ? 0 : pct, 0, 100);
        if (pct <= 0.2)
        {
            _arc.Visibility = Visibility.Collapsed;
            _full.Visibility = Visibility.Collapsed;
            return;
        }
        if (pct >= 99.8)
        {
            _arc.Visibility = Visibility.Collapsed;
            _full.Visibility = Visibility.Visible;
            return;
        }

        _full.Visibility = Visibility.Collapsed;
        _arc.Visibility = Visibility.Visible;

        var r = (_size - _thickness) / 2;
        var c = _size / 2;
        var sweep = pct / 100 * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(sweep), c - r * Math.Cos(sweep));
        var figure = new PathFigure { StartPoint = new Point(c, _thickness / 2), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(r, r),
            IsLargeArc = pct > 50,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _arc.Data = geometry;
    }
}
