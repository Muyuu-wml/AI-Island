using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AIIsland.UI;

/// <summary>Animated equalizer bars shown in the collapsed bar while music plays.</summary>
public sealed class PlayingBars : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(PlayingBars),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0xAF)), FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly DispatcherTimer timer;
    private double phase;

    public PlayingBars()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        timer.Tick += (_, _) =>
        {
            phase += 0.55;
            InvalidateVisual();
        };
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => timer.Stop();
        IsVisibleChanged += (_, _) => Sync();
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void Sync()
    {
        if (IsVisible) timer.Start();
        else timer.Stop();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var brush = Fill ?? Brushes.HotPink;
        const int bars = 4;
        const double gap = 2;
        var barWidth = Math.Max(1.5, (width - gap * (bars - 1)) / bars);
        var shortest = height * 0.3;

        for (var index = 0; index < bars; index++)
        {
            var level = 0.5 + 0.5 * Math.Sin(phase + index * 1.15);
            var barHeight = shortest + (height - shortest) * level;
            var rect = new Rect(index * (barWidth + gap), (height - barHeight) / 2, barWidth, barHeight);
            drawingContext.DrawRoundedRectangle(brush, null, rect, barWidth / 2, barWidth / 2);
        }
    }
}
