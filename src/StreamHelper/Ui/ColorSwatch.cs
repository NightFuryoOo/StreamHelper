using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using StreamHelper.Models;

namespace StreamHelper.Ui;

public static class ColorSwatch
{
    public static Button Create(FrameworkElement owner, string hex, string tooltip, string automationName, Action pick)
    {
        var (r, g, b) = HexColor.Parse(hex);
        var button = new Button
        {
            Style = (Style)owner.FindResource("FlatButton"),
            Padding = new Thickness(2),
            ToolTip = tooltip,
            Content = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(r, g, b)) },
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => pick();
        return button;
    }
}
