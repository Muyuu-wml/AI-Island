using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AIIsland.UI;

// Keep the original palette here so EnhancedOutline=false is a complete visual rollback.
public static class IslandAppearance
{
    private static ImageBrush? frostNoise;

    // A faint, low-contrast grain tiled over the panel so the translucent background reads as
    // frosted glass rather than clear glass. Generated once and frozen; the panel never blurs
    // what is behind it (no native acrylic), so this grain carries the texture.
    public static ImageBrush FrostNoise()
    {
        if (frostNoise is { } cached) return cached;
        const int size = 128;
        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size * size * 4];
        var random = new Random(20261012);
        for (var i = 0; i < size * size; i++)
        {
            var value = (byte)random.Next(0, 256);
            pixels[i * 4] = value; pixels[i * 4 + 1] = value; pixels[i * 4 + 2] = value; pixels[i * 4 + 3] = 255;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        frostNoise = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, size, size),
            Stretch = Stretch.Fill,
            Opacity = 0.08
        };
        frostNoise.Freeze();
        return frostNoise;
    }

    private static Brush FrostBackground(bool enhanced)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(enhanced ? "#E2323845" : "#E22A2E37"), 0));
        brush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(enhanced ? "#E21B2029" : "#E215171C"), 1));
        brush.Freeze();
        return brush;
    }

    public static void Apply(Border capsule, bool enhanced, bool hover, string state, bool animate, bool rainbow = false, string palette = "Rainbow", string customColor = "#72D8EE", string mode = "Static", bool lightAnimation = true, double thickness = 1)
    {
        static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
        capsule.BorderThickness = new System.Windows.Thickness(rainbow && double.IsFinite(thickness) ? Math.Clamp(thickness, 0.5, 6) : 1);
        capsule.Background = FrostBackground(enhanced);
        capsule.BorderBrush = new SolidColorBrush(ColorOf(enhanced ? hover ? "#B4C6DF" : "#76839A" : "#FF34363F"));
        if (rainbow)
        {
            var brush = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1), SpreadMethod = GradientSpreadMethod.Repeat };
            var colors = new[] { "#FF718B", "#FFBE70", "#EADC78", "#79DEA6", "#72D8EE", "#8F9CFF", "#D98AEE", "#FF718B" };
            if (palette == "Custom")
            {
                var chosen = IsHexColor(customColor) ? customColor : "#72D8EE";
                colors = mode == "Marquee" && lightAnimation ? new[] { chosen, "#FFFFFF", chosen } : new[] { chosen, chosen };
            }
            for (var i = 0; i < colors.Length; i++) brush.GradientStops.Add(new GradientStop(ColorOf(colors[i]), (double)i / (colors.Length - 1)));
            if (lightAnimation && mode == "Marquee")
            {
                var motion = new TranslateTransform(); brush.RelativeTransform = motion;
                motion.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 2, TimeSpan.FromSeconds(5)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else if (lightAnimation && mode == "Breathing")
                brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            else brush.Freeze();
            capsule.BorderBrush = brush;
        }
        var active = enhanced && state != "idle";
        var color = state switch { "approval" => "#F3BD65", "failed" => "#F18B91", _ => "#83B7F4" };
        var shadow = new DropShadowEffect {
            Color = active ? ColorOf(color) : Colors.Black,
            BlurRadius = 14, ShadowDepth = active ? 0 : 3,
            Opacity = active ? .28 : .35
        };
        capsule.Effect = shadow;
        if (active && animate)
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(.52, .28, TimeSpan.FromMilliseconds(650)) { FillBehavior = FillBehavior.Stop });
    }
    public static bool IsHexColor(string? value) => value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$");
}
