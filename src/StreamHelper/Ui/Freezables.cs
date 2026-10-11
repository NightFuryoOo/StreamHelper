using System.Windows;
using System.Windows.Media;

namespace StreamHelper.Ui;

internal static class Freezables
{
    public static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public static SolidColorBrush Solid(Color color) => Frozen(new SolidColorBrush(color));
}