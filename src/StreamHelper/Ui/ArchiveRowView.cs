using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Api;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public interface IArchiveRowHost
{
    ChatEmotes Emotes { get; }
    EmoteAnimator Animator { get; }
    void OpenHistory(ChatMessage message);
    void OpenLink(string url);
}

public sealed class ArchiveRowView : Border
{
    public const double TextSize = 14;

    private static readonly Brush TimeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA0)));
    private static readonly Brush MarkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush CardBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x33)));
    private static readonly Brush CardHoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x31, 0x36, 0x44)));
    private static readonly Brush PlaceholderBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

    private static readonly Dictionary<ArchiveEventTone, (Brush Accent, Brush Back)> Tones = new()
    {
        [ArchiveEventTone.Donation] = Tone(0xFFB020, 0x2A2417),
        [ArchiveEventTone.Subscription] = Tone(0xA970FF, 0x251F33),
        [ArchiveEventTone.Gift] = Tone(0xC792FF, 0x251F33),
        [ArchiveEventTone.Bits] = Tone(0x4FD1C5, 0x1A2B2B),
        [ArchiveEventTone.Reward] = Tone(0x4C9AFF, 0x1B2433),
        [ArchiveEventTone.Raid] = Tone(0x3DDC84, 0x1A2A21),
        [ArchiveEventTone.Clip] = Tone(0xFF7AC6, 0x2B1E29),
    };

    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(ArchiveRow), typeof(ArchiveRowView), new PropertyMetadata(null, (d, _) => ((ArchiveRowView)d).Rebuild()));

    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(IArchiveRowHost), typeof(ArchiveRowView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, _) => (d as ArchiveRowView)?.Rebuild()));

    private int _version;

    public static void SetHost(DependencyObject element, IArchiveRowHost? host) => element.SetValue(HostProperty, host);

    public static IArchiveRowHost? GetHost(DependencyObject element) => (IArchiveRowHost?)element.GetValue(HostProperty);

    public ArchiveRow? Row
    {
        get => (ArchiveRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private static (Brush, Brush) Tone(int accent, int back) => (Rgb(accent), Rgb(back));

    private static Brush Rgb(int rgb) => Frozen(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));

    private static string TimeOf(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");

    private void Rebuild()
    {
        _version++;
        var row = Row;
        var host = GetHost(this);
        if (row == null || host == null)
        {
            Child = null;
            return;
        }
        Child = row.Chat is { } chat ? BuildChat(row, chat, host) : BuildEvent(row, host);
    }

    private UIElement BuildChat(ArchiveRow row, ChatMessage chat, IArchiveRowHost host)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = TextSize, Margin = new Thickness(6, 2, 6, 2) };
        if (chat.Reply is { } parent)
        {
            text.Inlines.Add(new Run("↪ Ответ для " + parent.ParentLabel + (parent.ParentText.Length > 0 ? ": " + Short(parent.ParentText) : "")) { Foreground = TimeBrush, FontSize = 12 });
            text.Inlines.Add(new LineBreak());
        }
        text.Inlines.Add(new Run(TimeOf(chat.AtUtc) + "  ") { Foreground = TimeBrush, FontSize = 12 });
        var name = chat.ChatterName.Length > 0 ? chat.ChatterName : chat.ChatterLogin;
        var (r, g, b) = ChatColors.Readable(chat.Color, chat.ChatterLogin.Length > 0 ? chat.ChatterLogin : name);
        var nick = new Hyperlink(new Run(name))
        {
            Foreground = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b))),
            FontWeight = FontWeights.SemiBold,
            TextDecorations = null,
            Cursor = Cursors.Hand,
            ToolTip = "Все сообщения " + name,
        };
        nick.MouseEnter += (_, _) => nick.TextDecorations = TextDecorations.Underline;
        nick.MouseLeave += (_, _) => nick.TextDecorations = null;
        nick.Click += (_, _) => host.OpenHistory(chat);
        text.Inlines.Add(nick);
        text.Inlines.Add(new Run(": "));
        foreach (var inline in host.Emotes.Inlines(chat, TextSize, host.Animator))
        {
            if (row.Deleted)
            {
                inline.TextDecorations = TextDecorations.Strikethrough;
                if (inline is InlineUIContainer { Child: { } picture }) picture.Opacity = 0.55;
                else inline.Foreground = TimeBrush;
            }
            text.Inlines.Add(inline);
        }
        if (row.Moderation is { } moderation)
        {
            text.Inlines.Add(new Run("  · " + ChatModerationLabel.Unbroken(moderation)) { Foreground = MarkBrush, FontSize = 12, ToolTip = ChatModerationLabel.Tip(moderation) });
        }
        else if (row.Deleted) text.Inlines.Add(new Run("  · удалено") { Foreground = MarkBrush, FontSize = 12 });
        if (!host.Emotes.IsComplete(chat)) _ = FillLateEmotesAsync(row, chat, host, _version);
        return text;
    }

    private async Task FillLateEmotesAsync(ArchiveRow row, ChatMessage chat, IArchiveRowHost host, int version)
    {
        var shown = host.Emotes.Shown(chat);
        await host.Emotes.CompleteAsync(chat);
        if (version != _version || !ReferenceEquals(Row, row) || host.Emotes.Shown(chat) <= shown) return;
        Rebuild();
    }

    private static string Short(string text) => text.Length > 120 ? text[..120].TrimEnd() + "…" : text;

    private static UIElement BuildEvent(ArchiveRow row, IArchiveRowHost host)
    {
        var text = row.Event!;
        var (accent, back) = Tones[text.Tone];
        var stack = new StackPanel();
        var head = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        head.Inlines.Add(new Run(TimeOf(row.AtUtc) + "  ") { Foreground = TimeBrush, FontSize = 12 });
        head.Inlines.Add(new Run(text.Label) { Foreground = accent, FontWeight = FontWeights.Bold });
        if (text.Head.Length > 0) head.Inlines.Add(new Run("   " + text.Head) { FontWeight = FontWeights.SemiBold });
        stack.Children.Add(head);
        if (text.Body.Length > 0)
        {
            stack.Children.Add(new TextBlock { Text = text.Body, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 3, 0, 0) });
        }
        if (text.VideoUrl.Length > 0) stack.Children.Add(VideoCard(text, host));
        return new Border
        {
            Background = back,
            BorderBrush = accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(2, 4, 2, 4),
            Child = stack,
        };
    }

    public const double ThumbnailWidth = 112;
    public const double ThumbnailHeight = 63;

    private static readonly Geometry ThumbnailClip = Frozen(new RectangleGeometry(new Rect(0, 0, ThumbnailWidth, ThumbnailHeight), 4, 4));

    internal static Border VideoCard(ArchiveEventText text, IArchiveRowHost host)
    {
        var url = text.VideoUrl;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
        Grid.SetColumn(info, 1);
        if (Picture(text.VideoThumbnail) is { } picture)
        {
            var image = new Image { Source = picture, Stretch = Stretch.UniformToFill, Clip = ThumbnailClip };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            grid.Children.Add(new Border
            {
                Width = ThumbnailWidth,
                Height = ThumbnailHeight,
                CornerRadius = new CornerRadius(4),
                Background = PlaceholderBrush,
                Child = image,
            });
        }
        else info.Margin = new Thickness(6, 2, 6, 2);
        info.Children.Add(new TextBlock
        {
            Text = text.VideoTitle.Length > 0 ? text.VideoTitle : url,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 36,
        });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        label.Children.Add(new Viewbox
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text.VideoIsClip ? BrandIcons.Twitch() : BrandIcons.YouTube(),
        });
        label.Children.Add(new TextBlock
        {
            Text = text.VideoLabel.Length > 0 ? text.VideoLabel : "Видео",
            FontSize = 11,
            Foreground = TimeBrush,
            VerticalAlignment = VerticalAlignment.Center,
        });
        info.Children.Add(label);
        grid.Children.Add(info);
        var card = new Border
        {
            Background = CardBrush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 6, 0, 0),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
            ToolTip = url,
            Child = grid,
        };
        AutomationProperties.SetName(card, "Видео: " + (text.VideoTitle.Length > 0 ? text.VideoTitle : url));
        card.MouseEnter += (_, _) => card.Background = CardHoverBrush;
        card.MouseLeave += (_, _) => card.Background = CardBrush;
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            host.OpenLink(url);
        };
        return card;
    }

    private static BitmapImage? Picture(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.DecodePixelWidth = (int)(ThumbnailWidth * 2);
        image.EndInit();
        return image;
    }
}
