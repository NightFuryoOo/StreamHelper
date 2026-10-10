using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private static readonly Brush MentionPickedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x9B, 0x8C, 0xFF)));

    private readonly RecentChatters _chatters = new();
    private IReadOnlyList<Chatter> _mentionItems = Array.Empty<Chatter>();
    private MentionToken? _mentionToken;
    private int _mentionIndex;
    private int? _mentionDismissedAt;

    private bool MentionsShown => MentionBox.Visibility == Visibility.Visible;

    private void RememberChatter(ChatMessage message) =>
        _chatters.Saw(message.ChatterLogin, message.ChatterName, message.Color);

    private void UpdateMentions()
    {
        var token = _typing ? MentionText.At(ComposeText.Text, ComposeText.CaretIndex) : null;
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
        var items = _chatters.Match(token.Value.Prefix, _settings.Current.TwitchLogin);
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

    private void RenderMentions()
    {
        MentionList.Children.Clear();
        var size = ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize);
        for (var i = 0; i < _mentionItems.Count; i++)
        {
            var chatter = _mentionItems[i];
            var name = MentionText.NameFor(chatter);
            var (r, g, b) = ChatColors.Readable(chatter.Color, chatter.Login);
            var index = i;
            var row = new Border
            {
                Background = i == _mentionIndex ? MentionPickedBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3, 8, 3),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = name,
                    FontSize = size,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
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
        _mentionItems = Array.Empty<Chatter>();
        _mentionToken = null;
    }

    private void ChooseMention(int index)
    {
        if (_mentionToken is not { } token || index < 0 || index >= _mentionItems.Count) return;
        var (text, caret) = MentionText.Insert(ComposeText.Text, token, MentionText.NameFor(_mentionItems[index]));
        HideMentions();
        if (text.Length > ComposeText.MaxLength) return;
        ComposeText.Text = text;
        ComposeText.CaretIndex = caret;
        ComposeText.Focus();
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
