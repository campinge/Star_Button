using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ScMapper.App.Controls;

/// <summary>The slow accent glow that marks the button the user has to press next.</summary>
public static class Glow
{
    public static void Start(UIElement target)
    {
        var glow = new DropShadowEffect
        {
            Color = ((SolidColorBrush)Application.Current.FindResource("AccentBrush")).Color,
            ShadowDepth = 0, Opacity = 0.9, BlurRadius = 6,
        };
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(6, 22, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        target.Effect = glow;
    }

    public static void Stop(UIElement target) => target.Effect = null;
}
