using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private List<Inline> BadgeInlines(ChatMessage message, double fontSize)
    {
        var inlines = new List<Inline>();
        if (message.Badges == null) return inlines;
        foreach (var badge in message.Badges)
        {
            var url = _badges.UrlFor(badge.SetId, badge.Id);
            if (url == null) continue;
            var image = new Image { Source = ImageFor(url), Width = BadgeSize(fontSize), Height = BadgeSize(fontSize), Margin = new Thickness(0, 0, 4, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
        return inlines;
    }

    private void OnBadgesLoaded() => Dispatcher.InvokeAsync(AddMissingBadges);

    private void AddMissingBadges()
    {
        foreach (var line in _lines)
        {
            if (line.Message.Badges is not { Count: > 0 }) continue;
            if (line.Text.Inlines.OfType<InlineUIContainer>().Any(i => i.Child is Image { Tag: not "emote" })) continue;
            var first = line.Text.Inlines.FirstInline;
            foreach (var badge in BadgeInlines(line.Message, line.Text.FontSize)) line.Text.Inlines.InsertBefore(first, badge);
        }
    }

    private static double BadgeSize(double fontSize) => Math.Round(fontSize * 1.2);

    private BitmapImage ImageFor(string url)
    {
        if (_images.TryGetValue(url, out var cached)) return cached;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(url);
        image.CacheOption = BitmapCacheOption.OnDemand;
        image.EndInit();
        _images[url] = image;
        return image;
    }
}
