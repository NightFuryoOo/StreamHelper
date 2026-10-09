using System;

namespace StreamHelper.Ui;

public static class ChatPlacement
{
    public const double DefaultWidth = 380;
    public const double DefaultHeight = 300;
    public const double MinWidth = 200;
    public const double MinHeight = 90;
    public const double Margin = 16;
    public const double MinOpacity = 0.0;
    public const double DefaultOpacity = 0.6;
    public const double DefaultCellOpacity = 0.2;
    public const double DefaultOutlineOpacity = 0.9;
    public const double MinFontSize = 10;
    public const double MaxFontSize = 28;
    public const double DefaultFontSize = 14;

    public const int MinMuteMinutes = 1;
    public const int MaxMuteMinutes = 100;
    public const int DefaultMuteMinutes = 10;

    public static int ClampMuteMinutes(int value) =>
        value <= 0 ? DefaultMuteMinutes : Math.Clamp(value, MinMuteMinutes, MaxMuteMinutes);

    public static string DescribeDuration(int seconds)
    {
        if (seconds % 86400 == 0) return seconds == 86400 ? "сутки" : $"{seconds / 86400} сут";
        if (seconds % 3600 == 0) return $"{seconds / 3600} ч";
        if (seconds % 60 == 0) return $"{seconds / 60} мин";
        return $"{seconds} с";
    }

    public static double ClampOpacity(double value) => double.IsNaN(value) ? DefaultOpacity : Math.Clamp(value, MinOpacity, 1.0);

    public static double ClampCellOpacity(double value) => double.IsNaN(value) ? DefaultCellOpacity : Math.Clamp(value, 0.0, 1.0);

    public static double ClampOutlineOpacity(double value) => double.IsNaN(value) ? DefaultOutlineOpacity : Math.Clamp(value, 0.0, 1.0);

    public static double ClampFontSize(double value) =>
        double.IsNaN(value) ? DefaultFontSize : Math.Round(Math.Clamp(value, MinFontSize, MaxFontSize));

    public static (double Left, double Top, double Width, double Height) Resolve(
        double? savedLeft, double? savedTop, double? savedWidth, double? savedHeight, ScreenBounds workArea, ScreenBounds virtualScreen)
    {
        var width = Math.Clamp(savedWidth ?? DefaultWidth, MinWidth, Math.Max(MinWidth, virtualScreen.Right - virtualScreen.Left));
        var height = Math.Clamp(savedHeight ?? DefaultHeight, MinHeight, Math.Max(MinHeight, virtualScreen.Bottom - virtualScreen.Top));

        double left, top;
        if (savedLeft is { } l && savedTop is { } t && IsReachable(l, t, width, virtualScreen))
        {
            left = l;
            top = t;
        }
        else
        {
            left = workArea.Left + Margin;
            top = workArea.Bottom - height - Margin;
        }

        left = Math.Max(virtualScreen.Left, Math.Min(left, virtualScreen.Right - width));
        top = Math.Max(virtualScreen.Top, Math.Min(top, virtualScreen.Bottom - height));
        return (left, top, width, height);
    }

    private static bool IsReachable(double left, double top, double width, ScreenBounds screen) =>
        left + width > screen.Left + 40 && left < screen.Right - 40 && top > screen.Top - 20 && top < screen.Bottom - 40;
}
