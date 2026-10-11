using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StreamHelper.Ui;

internal sealed class ChannelAvatar
{
    private readonly Ellipse _face;
    private readonly TextBlock _letter;

    public ChannelAvatar(string name, double size, double fontSize, Brush placeholder)
    {
        _face = new Ellipse { Width = size, Height = size, Fill = placeholder };
        _letter = new TextBlock
        {
            Text = Initial(name),
            Foreground = Brushes.White,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Root = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Root.Children.Add(_face);
        Root.Children.Add(_letter);
    }

    public Grid Root { get; }

    public void Show(ImageSource picture)
    {
        _face.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
        _letter.Visibility = Visibility.Collapsed;
    }

    public static string Initial(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed[..1].ToUpperInvariant() : "?";
    }
}