using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public static class BrandIcons
{
    private static readonly Brush TwitchPurple = Solid(Color.FromRgb(0x91, 0x46, 0xFF));
    public static readonly Brush YouTubeRed = Solid(Color.FromRgb(0xFF, 0x00, 0x33));
    private static readonly Geometry TwitchScreen = Frozen(Geometry.Parse("M6.857,1.714 H20.571 V11.143 L17.143,14.571 H13.714 L10.714,17.571 V14.571 H6.857 Z"));
    private static readonly Geometry TwitchMark = Frozen(Geometry.Parse(
        "M11.571,4.714 H13.286 V9.857 H11.571 Z M16.286,4.714 H18 V9.857 H16.286 Z M6,0 L1.714,4.286 V19.714 H6.857 V24 L11.143,19.714 H14.571 L22.286,12 V0 Z " +
        "M20.571,11.143 L17.143,14.571 H13.714 L10.714,17.571 V14.571 H6.857 V1.714 H20.571 Z"));
    public static readonly Geometry YouTubeBox = Frozen(Geometry.Parse(
        "M23.5,6.2 C23.2,5.1 22.4,4.3 21.3,4 C19.4,3.5 12,3.5 12,3.5 C12,3.5 4.6,3.5 2.7,4 C1.6,4.3 0.8,5.1 0.5,6.2 C0,8.1 0,12 0,12 C0,12 0,15.9 0.5,17.8 " +
        "C0.8,18.9 1.6,19.7 2.7,20 C4.6,20.5 12,20.5 12,20.5 C12,20.5 19.4,20.5 21.3,20 C22.4,19.7 23.2,18.9 23.5,17.8 C24,15.9 24,12 24,12 C24,12 24,8.1 23.5,6.2 Z"));
    public static readonly Geometry YouTubePlay = Frozen(Geometry.Parse("M9.6,15.6 L15.8,12 L9.6,8.4 Z"));

    public static Canvas Twitch() => Icon(TwitchScreen, Brushes.White, TwitchMark, TwitchPurple);

    public static Canvas YouTube() => Icon(YouTubeBox, YouTubeRed, YouTubePlay, Brushes.White);

    private static Canvas Icon(Geometry back, Brush backFill, Geometry front, Brush frontFill) => new()
    {
        Width = 24,
        Height = 24,
        Children =
        {
            new Path { Data = back, Fill = backFill },
            new Path { Data = front, Fill = frontFill },
        },
    };
}
