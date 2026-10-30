using System.Windows;
using System.Windows.Media;

namespace AIIsland.UI;

/// <summary>Small circular gauge used in the compact bar and quota cards.</summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(double?), typeof(UsageRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(UsageRing),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(3.5, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Draws a solid accent ring when there is no percentage to show (balance-only sources).</summary>
    public static readonly DependencyProperty FullProperty = DependencyProperty.Register(
        nameof(Full), typeof(bool), typeof(UsageRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Percent
    {
        get => (double?)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool Full
    {
        get => (bool)GetValue(FullProperty);
        set => SetValue(FullProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        var track = new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), Thickness);
        track.Freeze();
        drawingContext.DrawEllipse(null, track, center, radius, radius);

        var percent = Percent;
        if (percent is null)
        {
            if (!Full) return;
            var ring = new Pen(Accent ?? Brushes.DodgerBlue, Thickness);
            ring.Freeze();
            drawingContext.DrawEllipse(null, ring, center, radius, radius);
            return;
        }
        var sweep = Math.Clamp(percent.Value, 0, 100) / 100 * 359.9;
        if (sweep <= 0.1) return;

        var pen = new Pen(Accent ?? Brushes.DodgerBlue, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var start = -90.0;
        var end = start + sweep;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var startPoint = PointOn(center, radius, start);
            context.BeginFigure(startPoint, false, false);
            context.ArcTo(PointOn(center, radius, end), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOn(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
