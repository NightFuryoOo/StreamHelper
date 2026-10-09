using System;
using System.Windows;

namespace StreamHelper.Ui;

internal static class AppIcon
{
    private static readonly Uri Source = new("/StreamHelper;component/app_icon.ico", UriKind.Relative);

    public static System.Drawing.Icon Load(System.Drawing.Size size)
    {
        var info = Application.GetResourceStream(Source) ?? throw new InvalidOperationException("The program icon is missing.");
        using var stream = info.Stream;
        return new System.Drawing.Icon(stream, size);
    }
}