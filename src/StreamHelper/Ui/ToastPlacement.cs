using System;

namespace StreamHelper.Ui;

public readonly record struct ScreenBounds(double Left, double Top, double Right, double Bottom);

public static class ToastPlacement
{
    public const double Margin = 16;
    public const double MinOpacity = 0.2;
    public const double MinScale = 0.6;
    public const double MaxScale = 2.0;
    public const int MinSeconds = 2;
    public const int MaxSeconds = 60;
    public const int DefaultSeconds = 6;

    public static double ClampOpacity(double value) => double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinOpacity, 1.0);

    public static double ClampScale(double value) => double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinScale, MaxScale);

    public static int ClampSeconds(double value) =>
        double.IsNaN(value) ? DefaultSeconds : (int)Math.Round(Math.Clamp(value, MinSeconds, MaxSeconds));

    public static (double Left, double Top) Resolve(
        double? savedLeft, double? savedTop, double width, double height, ScreenBounds workArea, ScreenBounds virtualScreen)
    {
        double left, top;
        if (savedLeft is { } l && savedTop is { } t && IsReachable(l, t, width, virtualScreen))
        {
            left = l;
            top = t;
        }
        else
        {
            left = workArea.Right - width - Margin;
            top = workArea.Top + Margin;
        }

        left = Math.Max(virtualScreen.Left, Math.Min(left, virtualScreen.Right - width));
        top = Math.Max(virtualScreen.Top, Math.Min(top, virtualScreen.Bottom - height));
        return (left, top);
    }

    private static bool IsReachable(double left, double top, double width, ScreenBounds screen) =>
        left + width > screen.Left + 40 && left < screen.Right - 40 && top > screen.Top - 20 && top < screen.Bottom - 40;
}
