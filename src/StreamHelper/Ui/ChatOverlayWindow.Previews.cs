using System;
using System.Threading.Tasks;
using System.Windows;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private static readonly TimeSpan PreviewWait = TimeSpan.FromSeconds(3);

    private readonly LinkPreviewCatalog? _previews;

    private bool PreviewsOn => _previews != null && _settings.Current.ChatLinkPreviews;

    private Task PreloadPreviewAsync(ChatMessage message)
    {
        if (!PreviewsOn || LinkPreviewCatalog.TargetOf(message) is not { } target) return Task.CompletedTask;
        return Task.WhenAny(_previews!.LoadAsync(target), Task.Delay(PreviewWait));
    }

    private void ShowPreview(ChatLine line)
    {
        if (!PreviewsOn || line.Card != null || LinkPreviewCatalog.TargetOf(line.Message) is not { } target) return;
        if (_previews!.Get(target) is { } preview) AddCard(line, target, preview);
        else if (!_previews.IsSettled(target)) _ = AddCardLaterAsync(line, target);
    }

    private async Task AddCardLaterAsync(ChatLine line, PreviewTarget target)
    {
        var preview = await _previews!.LoadAsync(target);
        if (preview == null || !PreviewsOn || line.Card != null || !_lines.Contains(line)) return;
        AddCard(line, target, preview);
    }

    private void AddCard(ChatLine line, PreviewTarget target, LinkPreview preview)
    {
        if (LinkCard.TryCreate(preview, _previews!.Thumbnail(preview), target.Url, ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize), OpenFromChat) is not { } card) return;
        card.Root.IsHitTestVisible = !_positioning;
        if (_deletedIds.Contains(line.Message.MessageId)) card.Root.Opacity = 0.55;
        line.Body.Children.Add(card.Root);
        line.Card = card;
    }

    private void OpenFromChat(string url)
    {
        if (!_interactive || _positioning) return;
        RestartIdle();
        BrowserLauncher.Open(url);
    }

    private void ApplyPreviews(double size)
    {
        foreach (var line in _lines)
        {
            if (line.Card == null)
            {
                ShowPreview(line);
                continue;
            }
            line.Card.Resize(size);
            line.Card.Root.Visibility = PreviewsOn ? Visibility.Visible : Visibility.Collapsed;
            line.Card.Root.IsHitTestVisible = !_positioning;
        }
    }
}
