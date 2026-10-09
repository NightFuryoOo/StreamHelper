using System;

namespace StreamHelper.Ui;

public static class MuteBadgePlacement
{
    public const double BaseSize = 40;
    public const double MinScale = 0.6;
    public const double MaxScale = 4.0;
    public const double Margin = 6;

    public static double ClampScale(double value) => double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinScale, MaxScale);

    public static (double Left, double Top) Resolve(
        double? savedLeft, double? savedTop, double size, ScreenBounds workArea, ScreenBounds virtualScreen)
    {
        double left, top;
        if (savedLeft is { } l && savedTop is { } t && !double.IsNaN(l) && !double.IsNaN(t) &&
            l + size > virtualScreen.Left && l < virtualScreen.Right && t + size > virtualScreen.Top && t < virtualScreen.Bottom)
        {
            left = l;
            top = t;
        }
        else
        {
            left = workArea.Right - size - Margin;
            top = workArea.Top + Margin;
        }

        left = Math.Max(virtualScreen.Left, Math.Min(left, virtualScreen.Right - size));
        top = Math.Max(virtualScreen.Top, Math.Min(top, virtualScreen.Bottom - size));
        return (left, top);
    }
}