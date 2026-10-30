using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIIsland.Core;
using AIIsland.Core.Monitoring;

namespace AIIsland.UI;

/// <summary>Daily token bars for the last few days, today on the right.</summary>
public sealed class UsageBars : FrameworkElement
{
    private const double LabelHeight = 12;
    private const double Gap = 3;
    private static readonly string[] WeekDays = { "日", "一", "二", "三", "四", "五", "六" };

    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<DailyUsage>), typeof(UsageBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(UsageBars),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(UsageBars),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DailyUsage>? Days
    {
        get => (IReadOnlyList<DailyUsage>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days;
        var width = ActualWidth;
        var height = ActualHeight - LabelHeight;
        if (days is null || days.Count == 0 || width <= 1 || height <= 1) return;

        // A transparent background so the whole area receives mouse moves for the tooltip.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var max = Math.Max(1, days.Max(day => day.Tokens));
        var barWidth = (width - Gap * (days.Count - 1)) / days.Count;
        var faded = Faded(Fill);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var x = index * (barWidth + Gap);
            var isToday = index == days.Count - 1;

            // Empty days still get a 1px stub so the row of days stays readable.
            var barHeight = day.Tokens > 0 ? Math.Max(2, height * day.Tokens / max) : 1;
            dc.DrawRoundedRectangle(isToday ? Fill : faded, null,
                new Rect(x, height - barHeight, barWidth, barHeight), 1.5, 1.5);

            var label = new FormattedText(isToday ? "今" : WeekDays[(int)day.Date.DayOfWeek],
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 8.5, LabelBrush, dpi);
            dc.DrawText(label, new Point(x + (barWidth - label.Width) / 2, height + 1));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var days = Days;
        if (days is null || days.Count == 0 || ActualWidth <= 0) return;
        var index = (int)Math.Clamp(e.GetPosition(this).X / ActualWidth * days.Count, 0, days.Count - 1);
        var day = days[index];
        var text = $"{day.Date:M月d日}  {Human.Tokens(day.Tokens)}";
        if (day.Cost > 0) text += " · " + Human.Money(day.Cost);
        if (!Equals(ToolTip, text)) ToolTip = text;
    }

    private static Brush Faded(Brush brush)
    {
        var color = (brush as SolidColorBrush)?.Color ?? Colors.DodgerBlue;
        var faded = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B));
        faded.Freeze();
        return faded;
    }
}
