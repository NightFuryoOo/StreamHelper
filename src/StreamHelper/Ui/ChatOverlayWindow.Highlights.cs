using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private static FrameworkElement PaletteIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M8,1.6 C4.2,1.6 1.6,4.4 1.6,8 C1.6,11.6 4.4,14.4 8,14.4 C9.2,14.4 9.6,13.5 9,12.7 C8.4,11.9 9,11 10.1,11 H12.2 C13.5,11 14.4,10.1 14.4,8.8 C14.4,4.6 11.6,1.6 8,1.6 Z"),
            Stroke = TextBrush,
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
        });
        void Spot(double x, double y, Color color)
        {
            var spot = new Ellipse { Width = 2.6, Height = 2.6, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(spot, x - 1.3);
            Canvas.SetTop(spot, y - 1.3);
            canvas.Children.Add(spot);
        }
        Spot(4.9, 6.4, Color.FromRgb(0xFF, 0x45, 0x3A));
        Spot(8.0, 4.5, Color.FromRgb(0xFF, 0xD6, 0x0A));
        Spot(11.1, 6.4, Color.FromRgb(0x30, 0xD1, 0x58));
        Spot(4.9, 9.7, Color.FromRgb(0x0A, 0x84, 0xFF));
        return new Viewbox { Width = 18, Height = 18, Child = canvas };
    }

    private string? HighlightOf(ChatMessage message) =>
        _settings.Current.ChatHighlights.TryGetValue(ChatFeed.KeyOf(message), out var hex) ? HexColor.Normalize(hex) : null;

    private double MarkOpacity() => ChatPlacement.ClampCellOpacity(_settings.Current.ChatCellOpacity);

    private void ApplyPingBrushes()
    {
        var settings = _settings.Current;
        var (r, g, b) = HexColor.Parse(ChatHighlights.PingColor(settings));
        var color = Color.FromRgb(r, g, b);
        _pingText.Color = color;
        _pingLine.Color = color;
        _pingLine.Opacity = ChatHighlights.PingLineOpacity;
    }

    private Brush LineFor(ChatMessage message) => ChatHighlights.MarksPing(_settings.Current, message) ? _pingLine : _cellLine;

    private Brush TextFor(ChatMessage message) => ChatHighlights.MarksPing(_settings.Current, message) ? _pingText : TextBrush;

    private Brush PlateFor(ChatMessage message)
    {
        var hex = HighlightOf(message);
        if (hex == null) return _cellBack;
        if (!_markBrushes.TryGetValue(hex, out var brush))
        {
            var (r, g, b) = HexColor.Parse(hex);
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            _markBrushes[hex] = brush;
        }
        brush.Opacity = MarkOpacity();
        return brush;
    }

    private void SetHighlight(string key, string? hex)
    {
        var marks = _settings.Current.ChatHighlights;
        if (hex == null) marks.Remove(key);
        else marks[key] = hex;
        _settings.Save();
        foreach (var line in _lines)
        {
            if (ChatFeed.KeyOf(line.Message) == key) line.Box.Background = PlateFor(line.Message);
        }
    }

    private void ChooseCustomColor(ChatLine line)
    {
        _idle.Stop();
        var previous = NativeMethods.GetForegroundWindow();
        string? hex;
        try
        {
            hex = HighlightColorDialog.Ask(this, NameOf(line.Message), line.NickBrush, HighlightOf(line.Message));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != _handle && NativeMethods.Exists(previous)) NativeMethods.SetForegroundWindow(previous);
            RestartIdle();
        }
        if (hex != null) SetHighlight(ChatFeed.KeyOf(line.Message), hex);
    }
    private bool CanModerate(ChatMessage message) => ChatRules.CanModerate(message, _settings.Current);
}
