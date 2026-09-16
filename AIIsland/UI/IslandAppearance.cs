using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace AIIsland.UI;

// Keep the original palette here so EnhancedOutline=false is a complete visual rollback.
public static class IslandAppearance
{
    public static void Apply(Border capsule, bool enhanced, bool hover, string state, bool animate, bool rainbow = false, string palette = "Rainbow", string customColor = "#72D8EE", string mode = "Static", bool lightAnimation = true)
    {
        static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
        capsule.Background = new SolidColorBrush(ColorOf(enhanced ? "#252B38" : "#F018191E"));
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
