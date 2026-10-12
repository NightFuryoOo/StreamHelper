using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public static class LinkCardText
{
    public static string Views(int count) => ChannelLive.Count(count) + " " + Words.Plural(count, "просмотр", "просмотра", "просмотров");

    public static string Duration(TimeSpan length) =>
        length.TotalHours >= 1 ? $"{(int)length.TotalHours}:{length.Minutes:00}:{length.Seconds:00}" : $"{(int)length.TotalMinutes}:{length.Seconds:00}";

    public static string Meta(LinkPreview preview)
    {
        if (preview.Kind == PreviewKind.YouTube) return preview.Channel.Length > 0 ? preview.Channel : "YouTube";
        var meta = preview.Channel;
        if (preview.Views is { } views) meta += (meta.Length > 0 ? " · " : "") + Views(views);
        return meta.Length > 0 ? meta : "Twitch";
    }

    public static string Credit(LinkPreview preview) =>
        preview.Kind == PreviewKind.TwitchClip && preview.Credit.Length > 0 ? "Клип от " + preview.Credit : "";

    public static string Name(LinkPreview preview) => (preview.Kind == PreviewKind.TwitchClip ? "Клип: " : "Видео: ") + preview.Title;
}

public sealed class LinkCard
{
    private static readonly Brush Back = Solid(Color.FromArgb(0x70, 0x10, 0x12, 0x17));
    private static readonly Brush HoverBack = Solid(Color.FromArgb(0xB0, 0x10, 0x12, 0x17));
    private static readonly Brush TitleBrush = Solid(Color.FromRgb(0xE6, 0xE8, 0xEE));
    private static readonly Brush MetaBrush = Solid(Color.FromRgb(0x9A, 0xA3, 0xB2));
    private static readonly Brush BadgeBack = Solid(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
    private static readonly Brush NoPicture = Solid(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static ControlTemplate? _template;

    private readonly Border _picture;
    private readonly TextBlock _title;
    private readonly TextBlock _meta;
    private readonly TextBlock _credit;
    private readonly TextBlock _length;
    private readonly Viewbox _icon;

    public LinkCard(LinkPreview preview, EmoteFrames? thumbnail, string url, double fontSize, Action<string> open)
    {
        _length = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold };
        var badge = new Border
        {
            Background = BadgeBack,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0, 3, 0),
            Margin = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _length,
            Visibility = preview.Duration == null ? Visibility.Collapsed : Visibility.Visible,
        };
        if (preview.Duration is { } length) _length.Text = LinkCardText.Duration(length);
        _picture = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = thumbnail == null ? NoPicture : new ImageBrush(thumbnail.Frames[0]) { Stretch = Stretch.UniformToFill },
            Child = badge,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _title = new TextBlock { Text = preview.Title, Foreground = TitleBrush, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
        _meta = new TextBlock { Text = LinkCardText.Meta(preview), Foreground = MetaBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        _credit = new TextBlock { Text = LinkCardText.Credit(preview), Foreground = MetaBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        if (_credit.Text.Length == 0) _credit.Visibility = Visibility.Collapsed;
        _icon = new Viewbox { Child = preview.Kind == PreviewKind.TwitchClip ? BrandIcons.Twitch() : BrandIcons.YouTube(), Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var source = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { _icon, _meta } };
        var words = new StackPanel { Margin = new Thickness(8, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _title, source, _credit } };
        Grid.SetColumn(words, 1);
        var layout = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
            Children = { _picture, words },
        };

        Root = new Button
        {
            Template = Template(),
            Content = layout,
            Background = Back,
            Focusable = false,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 1),
            ToolTip = url,
        };
        AutomationProperties.SetName(Root, LinkCardText.Name(preview));
        Root.Click += (_, _) => open(url);
        Resize(fontSize);
    }

    public Button Root { get; }

    public static LinkCard? TryCreate(LinkPreview preview, EmoteFrames? thumbnail, string url, double fontSize, Action<string> open)
    {
        try
        {
            return new LinkCard(preview, thumbnail, url, fontSize, open);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Log.Write("Link card failed: " + ex.Message);
            return null;
        }
    }

    public void Resize(double fontSize)
    {
        var titleSize = Math.Round(fontSize * 0.9, 1);
        var metaSize = Math.Round(fontSize * 0.8, 1);
        _title.FontSize = titleSize;
        _title.MaxHeight = Math.Ceiling(titleSize * 1.34 * 2);
        _meta.FontSize = metaSize;
        _credit.FontSize = metaSize;
        _length.FontSize = Math.Round(fontSize * 0.7, 1);
        _icon.Width = _icon.Height = Math.Round(metaSize * 1.15);
        var width = Math.Round(fontSize * 8.5);
        _picture.Width = width;
        _picture.Height = Math.Round(width * 9 / 16);
        Root.MaxWidth = Math.Round(fontSize * 30);
    }


    private static ControlTemplate Template()
    {
        if (_template != null) return _template;
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBack, "bd"));
        template.Triggers.Add(hover);
        template.Seal();
        _template = template;
        return template;
    }
}
