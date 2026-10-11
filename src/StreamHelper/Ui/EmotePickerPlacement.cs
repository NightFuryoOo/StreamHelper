using System;
using System.Windows;

namespace StreamHelper.Ui;

public static class EmotePickerPlacement
{
    public const double Gap = 6;

    public static Point Place(Rect chat, Rect bounds, Size size)
    {
        double ClampLeft(double left) => Math.Max(bounds.Left, Math.Min(left, bounds.Right - size.Width));
        double ClampTop(double top) => Math.Max(bounds.Top, Math.Min(top, bounds.Bottom - size.Height));
        var sideTop = ClampTop(chat.Bottom - size.Height);

        if (chat.Right + Gap + size.Width <= bounds.Right) return new Point(chat.Right + Gap, sideTop);
        if (chat.Left - Gap - size.Width >= bounds.Left) return new Point(chat.Left - Gap - size.Width, sideTop);
        if (chat.Top - Gap - size.Height >= bounds.Top) return new Point(ClampLeft(chat.Right - size.Width), chat.Top - Gap - size.Height);
        if (chat.Bottom + Gap + size.Height <= bounds.Bottom) return new Point(ClampLeft(chat.Right - size.Width), chat.Bottom + Gap);
        return new Point(ClampLeft(chat.Right - size.Width), ClampTop(chat.Bottom - size.Height));
    }
}
