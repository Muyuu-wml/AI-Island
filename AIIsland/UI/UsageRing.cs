using System;
using System.Windows;
using System.Windows.Media;

namespace AIIsland.UI;

public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        const double thickness = 5;
        var radius = Math.Max(0, Math.Min(ActualWidth, ActualHeight) / 2 - thickness / 2);
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var percent = double.IsFinite(Value) ? Math.Clamp(Value, 0, 100) : 0;
        drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(55, 66, 84)), thickness), center, radius, radius);
        var color = percent >= 90 ? Color.FromRgb(243, 189, 101) : Color.FromRgb(114, 216, 238);
        var pen = new Pen(new SolidColorBrush(color), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (percent >= 100) { drawing.DrawEllipse(null, pen, center, radius, radius); return; }
        if (percent <= 0) return;
        var angle = percent / 100 * Math.PI * 2;
        var end = new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius);
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            context.ArcTo(end, new Size(radius, radius), 0, percent > 50, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        drawing.DrawGeometry(null, pen, arc);
    }
}
