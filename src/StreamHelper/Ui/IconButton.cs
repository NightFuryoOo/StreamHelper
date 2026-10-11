using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace StreamHelper.Ui;

internal static class Glyphs
{
    public const string Mute = "M2,6 H4.8 L8.6,2.8 V13.2 L4.8,10 H2 Z M11,5.8 L14.2,10.2 M14.2,5.8 L11,10.2";
    public const string Clock = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M8,5 V8.2 L10.4,9.8";
    public const string Ban = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M3.8,3.8 L12.2,12.2";
    public const string Undo = "M5,4 L2.5,6.5 L5,9 M2.8,6.5 H9 A3.5,3.5 0 0 1 9,13.5 H6";
    public const string Check = "M3,8.5 L6.4,12 L13,4.5";
    public const string Trash = "M3.5,5 H12.5 M6.5,5 V3.5 H9.5 V5 M4.8,5 L5.4,13 H10.6 L11.2,5";
}

internal sealed class IconButton
{
    private readonly Path _glyph;
    private readonly TextBlock _label;
    private readonly string _idleGlyph;
    private readonly Brush _idleStroke;
    private string _name;

    public IconButton(string glyph, Brush stroke, Style style, string name)
    {
        _idleGlyph = glyph;
        _idleStroke = stroke;
        _name = name;
        _glyph = new Path
        {
            Data = Geometry.Parse(glyph),
            Stroke = stroke,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label = new TextBlock
        {
            Text = "Точно?",
            FontSize = 12,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_glyph);
        content.Children.Add(_label);
        Button = new Button { Content = content, Style = style, ToolTip = name };
        System.Windows.Automation.AutomationProperties.SetName(Button, name);
    }

    public Button Button { get; }

    public void Rename(string name)
    {
        _name = name;
        Button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(Button, name);
    }

    public void ShowConfirm(Brush brush)
    {
        _glyph.Data = Geometry.Parse(Glyphs.Check);
        _glyph.Stroke = brush;
        _label.Foreground = brush;
        _label.Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetName(Button, "Точно? " + _name);
    }

    public void ShowIdle()
    {
        _glyph.Data = Geometry.Parse(_idleGlyph);
        _glyph.Stroke = _idleStroke;
        _label.Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(Button, _name);
    }
}
