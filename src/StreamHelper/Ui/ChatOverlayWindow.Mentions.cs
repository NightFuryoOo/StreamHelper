using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private static readonly Brush MentionPickedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x9B, 0x8C, 0xFF)));

    private sealed record Suggestion(string Insert, string Label, Brush Foreground, string? ImageUrl);

    private readonly RecentChatters _chatters = new();
    private IReadOnlyList<Suggestion> _mentionItems = Array.Empty<Suggestion>();
    private MentionToken? _mentionToken;
    private int _mentionIndex;
    private int? _mentionDismissedAt;
    private bool _waitingForEmotes;
    private bool _waitingForChatters;
    private readonly ChatterDirectory? _present;

    private bool MentionsShown => MentionBox.Visibility == Visibility.Visible;

    private void RememberChatter(ChatMessage message) =>
        _chatters.Saw(message.ChatterLogin, message.ChatterName, message.Color);

    private Chatter? ChannelOwner()
    {
        var settings = _settings.Current;
        if (settings.IsOwnChannel || settings.TwitchChannelLogin.Length == 0) return null;
        return new Chatter(settings.TwitchChannelLogin, settings.TwitchChannelName.Length > 0 ? settings.TwitchChannelName : settings.TwitchChannelLogin, "");
    }

    private void UpdateMentions()
    {
        var text = ComposeText.Text;
        var caret = ComposeText.CaretIndex;
        var mention = _typing ? MentionText.At(text, caret) : null;
        var emote = _typing && mention == null ? MentionText.EmoteAt(text, caret) : null;
        var token = mention ?? emote;
        if (token == null)
        {
            _mentionDismissedAt = null;
            HideMentions();
            return;
        }
        if (_mentionDismissedAt == token.Value.Start)
        {
            HideMentions();
            return;
        }
        _mentionDismissedAt = null;
        var items = mention != null ? ChatterSuggestions(token.Value.Prefix) : EmoteSuggestions(token.Value.Prefix);
        if (items.Count == 0)
        {
            HideMentions();
            return;
        }
        if (_mentionToken?.Prefix != token.Value.Prefix || _mentionToken?.Start != token.Value.Start) _mentionIndex = 0;
        _mentionToken = token;
        _mentionItems = items;
        _mentionIndex = Math.Clamp(_mentionIndex, 0, items.Count - 1);
        RenderMentions();
        MentionBox.Visibility = Visibility.Visible;
    }

    private IReadOnlyList<Suggestion> ChatterSuggestions(string prefix)
    {
        if (_present != null && !_waitingForChatters)
        {
            var refresh = _present.RefreshAsync();
            if (!refresh.IsCompleted)
            {
                _waitingForChatters = true;
                _ = WaitForChattersAsync(refresh);
            }
        }
        return _chatters.Match(prefix, _settings.Current.TwitchLogin, pinned: ChannelOwner(), everyone: _present?.All)
            .Select(c =>
            {
                var (r, g, b) = ChatColors.Readable(c.Color, c.Login);
                var name = MentionText.NameFor(c);
                return new Suggestion("@" + name, name, new SolidColorBrush(Color.FromRgb(r, g, b)), null);
            })
            .ToList();
    }

    private async System.Threading.Tasks.Task WaitForChattersAsync(System.Threading.Tasks.Task refresh)
    {
        try
        {
            await refresh;
        }
        finally
        {
            _waitingForChatters = false;
        }
        if (_typing) UpdateMentions();
    }

    private IReadOnlyList<Suggestion> EmoteSuggestions(string prefix)
    {
        if (_emotes.Library is not { } library) return Array.Empty<Suggestion>();
        if (library.All.Count == 0 && !_waitingForEmotes)
        {
            _waitingForEmotes = true;
            _ = WaitForEmotesAsync(library);
        }
        return EmoteChoices.Match(library.All, prefix).Select(e => new Suggestion(e.Name, e.Name, TextBrush, e.Url)).ToList();
    }

    private async System.Threading.Tasks.Task WaitForEmotesAsync(EmoteLibrary library)
    {
        try
        {
            await library.EnsureLoadedAsync();
        }
        finally
        {
            _waitingForEmotes = false;
        }
        if (_typing) UpdateMentions();
    }

    private void RenderMentions()
    {
        MentionList.Children.Clear();
        var size = ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize);
        for (var i = 0; i < _mentionItems.Count; i++)
        {
            var item = _mentionItems[i];
            var index = i;
            var label = new TextBlock
            {
                Text = item.Label,
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = item.Foreground,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            FrameworkElement content = label;
            if (item.ImageUrl != null)
            {
                var image = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var cached = _emotes.CachedImage(item.ImageUrl);
                if (cached != null) image.Source = cached.Frames[0];
                else _ = LoadEmoteAsync(image, item.ImageUrl);
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(image);
                line.Children.Add(label);
                content = line;
            }
            var row = new Border
            {
                Background = i == _mentionIndex ? MentionPickedBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                Child = content,
            };
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ChooseMention(index);
            };
            MentionList.Children.Add(row);
        }
    }

    private void HideMentions()
    {
        if (MentionBox.Visibility == Visibility.Collapsed && _mentionItems.Count == 0) return;
        MentionBox.Visibility = Visibility.Collapsed;
        MentionList.Children.Clear();
        _mentionItems = Array.Empty<Suggestion>();
        _mentionToken = null;
    }

    private void ChooseMention(int index)
    {
        if (_mentionToken is not { } token || index < 0 || index >= _mentionItems.Count) return;
        var item = _mentionItems[index];
        var (text, caret) = MentionText.Replace(ComposeText.Text, token, item.Insert);
        HideMentions();
        if (text.Length > ComposeText.MaxLength) return;
        ComposeText.SetText(text, caret);
        ComposeText.Focus();
        if (item.ImageUrl != null)
        {
            _settings.Current.ChatRecentEmotes = EmoteChoices.Remember(_settings.Current.ChatRecentEmotes, item.Insert);
            _settings.Save();
        }
    }

    private bool HandleMentionKey(KeyEventArgs e)
    {
        if (!MentionsShown || _mentionItems.Count == 0) return false;
        switch (e.Key)
        {
            case Key.Down:
                _mentionIndex = (_mentionIndex + 1) % _mentionItems.Count;
                RenderMentions();
                return true;
            case Key.Up:
                _mentionIndex = (_mentionIndex - 1 + _mentionItems.Count) % _mentionItems.Count;
                RenderMentions();
                return true;
            case Key.Tab:
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                ChooseMention(_mentionIndex);
                return true;
            case Key.Escape:
                _mentionDismissedAt = _mentionToken?.Start;
                HideMentions();
                return true;
            default:
                return false;
        }
    }
}
