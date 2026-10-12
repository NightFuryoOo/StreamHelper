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

    public static readonly Brush LiveDot = Frozen(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.35, 0.3),
        Center = new Point(0.45, 0.4),
        RadiusX = 0.65,
        RadiusY = 0.65,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0x9A, 0x8F), 0),
            new GradientStop(Color.FromRgb(0xEB, 0x04, 0x00), 0.55),
            new GradientStop(Color.FromRgb(0xA8, 0x00, 0x00), 1),
        },
    });
}