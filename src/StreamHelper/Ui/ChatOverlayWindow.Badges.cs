using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
            var image = new Image { Width = BadgeSize(fontSize), Height = BadgeSize(fontSize), Margin = new Thickness(0, 0, 4, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (_emotes.CachedImage(url) is { } cached) image.Source = cached.Frames[0];
            else _ = ShowBadgeAsync(image, url);
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
            if (line.Text.Inlines.OfType<InlineUIContainer>().Any(i => i.Child is Image image && !ChatEmotes.IsEmote(image.Tag))) continue;
            var first = line.Text.Inlines.FirstInline;
            foreach (var badge in BadgeInlines(line.Message, line.Text.FontSize)) line.Text.Inlines.InsertBefore(first, badge);
        }
    }

    private static double BadgeSize(double fontSize) => Math.Round(fontSize * 1.2);

    private async Task ShowBadgeAsync(Image image, string url)
    {
        if (await _emotes.ImageAsync(url) is { } frames) image.Source = frames.Frames[0];
    }
}
