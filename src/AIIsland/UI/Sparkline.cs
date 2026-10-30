using System.Windows;
using System.Windows.Media;

namespace AIIsland.UI;

/// <summary>Small filled line chart for the last N percentage samples.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly List<double> values = new();

    public int Capacity { get; set; } = 60;
    public Brush Stroke { get; set; } = new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF));

    public void Push(double value)
    {
        value = Math.Clamp(value, 0, 100);
        if (values.Count == 0)
            for (var fill = 0; fill < Capacity - 1; fill++) values.Add(value);
        values.Add(value);
        if (values.Count > Capacity) values.RemoveRange(0, values.Count - Capacity);
        InvalidateVisual();
    }

    public void Clear()
    {
        values.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 1 || height <= 1) return;

        var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
        for (var line = 1; line <= 3; line++)
            dc.DrawLine(grid, new Point(0, height * line / 4), new Point(width, height * line / 4));

        if (values.Count < 2) return;

        var step = width / (Capacity - 1);
        var points = values.Select((value, index) =>
            new Point(index * step, height - 2 - value / 100 * (height - 4))).ToArray();

        var area = new StreamGeometry();
        using (var context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, height), true, true);
            foreach (var point in points) context.LineTo(point, false, false);
            context.LineTo(new Point(points[^1].X, height), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(CreateFill(), null, area);

        var curve = new StreamGeometry();
        using (var context = curve.Open())
        {
            context.BeginFigure(points[0], false, false);
            foreach (var point in points.Skip(1)) context.LineTo(point, true, true);
        }
        curve.Freeze();
        var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, pen, curve);
    }

    private Brush CreateFill()
    {
        var color = (Stroke as SolidColorBrush)?.Color ?? Color.FromRgb(0x5B, 0x8C, 0xFF);
        return new LinearGradientBrush(
            Color.FromArgb(0x40, color.R, color.G, color.B),
            Color.FromArgb(0x00, color.R, color.G, color.B),
            new Point(0, 0), new Point(0, 1));
    }
}
