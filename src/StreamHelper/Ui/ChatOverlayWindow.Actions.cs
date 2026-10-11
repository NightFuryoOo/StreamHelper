using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private void ShowNickMenu(ChatLine line)
    {
        var key = ChatFeed.KeyOf(line.Message);
        var current = HighlightOf(line.Message);
        var menu = new ContextMenu
        {
            PlacementTarget = line.Text,
            Placement = PlacementMode.MousePoint,
        };

        var nick = new MenuItem { Header = ChatRules.MenuText(NameOf(line.Message)), IsEnabled = false, FontWeight = FontWeights.Bold };
        System.Windows.Automation.AutomationProperties.SetName(nick, NameOf(line.Message));
        menu.Items.Add(nick);

        var history = new MenuItem { Header = "История" };
        System.Windows.Automation.AutomationProperties.SetName(history, "История");
        history.Click += (_, _) => HistoryRequested?.Invoke(line.Message);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.Items.Add(history);
        if (line.Message.MessageId.Length > 0)
        {
            var reply = Item("Ответить");
            reply.Click += (_, _) => Dispatcher.InvokeAsync(() => ReplyTo(line.Message));
            menu.Items.Add(reply);
        }
        if (CanModerate(line.Message)) menu.Items.Add(ShoutoutItem(menu, line));

        var label = new MenuItem { Header = "Подсветить", IsEnabled = false };
        System.Windows.Automation.AutomationProperties.SetName(label, "Подсветить");
        menu.Items.Add(label);

        var flat = (Style)Application.Current.Resources["FlatButton"];
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var swatch in ChatHighlights.Palette)
        {
            var (r, g, b) = HexColor.Parse(swatch.Hex);
            var square = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = string.Equals(current, swatch.Hex, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent,
                BorderThickness = new Thickness(2),
            };
            var button = new Button { Style = flat, Content = square, Padding = new Thickness(3), ToolTip = swatch.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, swatch.Name);
            var hex = swatch.Hex;
            button.Click += (_, _) =>
            {
                menu.IsOpen = false;
                SetHighlight(key, hex);
            };
            strip.Children.Add(button);
        }
        var more = new Button { Style = flat, Content = PaletteIcon(), Padding = new Thickness(3), ToolTip = "Другой цвет" };
        System.Windows.Automation.AutomationProperties.SetName(more, "Другой цвет");
        more.Click += (_, _) =>
        {
            menu.IsOpen = false;
            Dispatcher.InvokeAsync(() => ChooseCustomColor(line));
        };
        strip.Children.Add(more);

        var swatches = new MenuItem { Header = strip };
        menu.Items.Add(swatches);

        if (current != null)
        {
            var remove = new MenuItem { Header = "Убрать подсветку" };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Убрать подсветку");
            remove.Click += (_, _) => SetHighlight(key, null);
            menu.Items.Add(remove);
        }
        AddChannelItems(menu);
        menu.IsOpen = true;
    }

    private MenuItem ShoutoutItem(ContextMenu menu, ChatLine line)
    {
        if (!_settings.Current.HasShoutoutScope) return Item("Отметить (нужно переподключить Twitch)", false);
        if (_shouting) return Item("Отметить (отправляю…)", false);
        var wait = _shoutouts.Check(line.Message.ChatterId, DateTime.UtcNow);
        var item = Item(ShoutoutLimits.MenuText(wait), wait.Allowed);
        if (!wait.Allowed) return item;

        item.StaysOpenOnClick = true;
        var armed = false;
        var timer = new DispatcherTimer { Interval = BanConfirmWindow };
        void Rest()
        {
            timer.Stop();
            armed = false;
            item.Header = ChatRules.MenuText("Отметить");
            item.ClearValue(ForegroundProperty);
            System.Windows.Automation.AutomationProperties.SetName(item, "Отметить");
        }
        timer.Tick += (_, _) => Rest();
        menu.Closed += (_, _) => timer.Stop();
        item.Click += async (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                item.Header = ChatRules.MenuText("Точно?");
                item.Foreground = DoneBrush;
                System.Windows.Automation.AutomationProperties.SetName(item, "Точно? Отметить");
                timer.Start();
                return;
            }
            timer.Stop();
            menu.IsOpen = false;
            await ShoutoutAsync(line);
        };
        return item;
    }

    private async Task ShoutoutAsync(ChatLine line)
    {
        var chatter = line.Message.ChatterId;
        if (_shouting) return;
        var wait = _shoutouts.Check(chatter, DateTime.UtcNow);
        if (!wait.Allowed)
        {
            ShowShout(line, ShoutoutLimits.MenuText(wait), ErrorBrush);
            return;
        }
        _shouting = true;
        ModerationResult result;
        try
        {
            result = await _moderation.ShoutoutAsync(chatter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Shoutout failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        finally
        {
            _shouting = false;
        }
        if (result.Success)
        {
            _shoutouts.Sent(chatter, DateTime.UtcNow);
            foreach (var shouted in LinesOf(chatter)) ShowShout(shouted, "отмечен", ShoutBrush);
        }
        else ShowShout(line, result.Message, ErrorBrush);
        RestartIdle();
    }

    private static void ShowShout(ChatLine line, string text, Brush brush)
    {
        if (line.Shout != null) line.Text.Inlines.Remove(line.Shout);
        line.Shout = new Run("  · " + text) { Foreground = brush, FontSize = line.Text.FontSize * 0.9 };
        line.Text.Inlines.Add(line.Shout);
    }

    private enum Armed
    {
        None,
        Mute,
        Ban,
        Delete,
    }

    private string MuteName() => "Мут на " + ChatPlacement.DescribeDuration(ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes) * 60);

    private void AddActions(ChatLine line)
    {
        var style = (Style)FindResource("ChatAction");
        var mute = new IconButton(Glyphs.Mute, TextBrush, style, MuteName());
        var custom = new IconButton(Glyphs.Clock, TextBrush, style, "Мут на своё время");
        var ban = new IconButton(Glyphs.Ban, ErrorBrush, style, "Бан");
        var undo = new IconButton(Glyphs.Undo, TextBrush, style, "Снять");
        undo.Button.Visibility = Visibility.Collapsed;
        var remove = new IconButton(Glyphs.Trash, TextBrush, style, "Удалить сообщение");
        if (line.Message.MessageId.Length == 0) remove.Button.Visibility = Visibility.Collapsed;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { remove, mute, custom, ban, undo }) buttons.Children.Add(button.Button);
        var panel = new Border
        {
            Child = buttons,
            Background = ActionsBack,
            BorderBrush = ActionsLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        line.Actions = panel;
        line.Mute = mute;
        line.Custom = custom;
        line.Ban = ban;
        line.Undo = undo;
        line.Remove = remove;

        remove.Button.Click += async (_, _) =>
        {
            if (line.Armed != Armed.Delete)
            {
                Arm(line, Armed.Delete);
                return;
            }
            await DeleteMessageAsync(line);
        };

        mute.Button.Click += async (_, _) =>
        {
            if (line.Armed != Armed.Mute)
            {
                Arm(line, Armed.Mute);
                return;
            }
            var seconds = ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes) * 60;
            await ModerateAsync(line, seconds, "замучен на " + ChatPlacement.DescribeDuration(seconds));
        };
        custom.Button.Click += async (_, _) => await MuteForChosenTimeAsync(line);
        ban.Button.Click += async (_, _) =>
        {
            if (line.Armed != Armed.Ban)
            {
                Arm(line, Armed.Ban);
                return;
            }
            await ModerateAsync(line, null, "забанен");
        };
        undo.Button.Click += async (_, _) => await LiftAsync(line);

        line.Root.Children.Add(panel);
        line.Box.MouseEnter += (_, _) =>
        {
            if (_interactive) panel.Visibility = Visibility.Visible;
        };
        line.Box.MouseLeave += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            Disarm(line);
        };
    }

    private async Task MuteForChosenTimeAsync(ChatLine line)
    {
        Disarm(line);
        _idle.Stop();
        var previous = NativeMethods.GetForegroundWindow();
        int? seconds;
        try
        {
            seconds = MuteDialog.Ask(this, NameOf(line.Message), line.NickBrush, ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != _handle && NativeMethods.Exists(previous)) NativeMethods.SetForegroundWindow(previous);
            RestartIdle();
        }
        if (seconds is { } chosen) await ModerateAsync(line, chosen, "замучен на " + ChatPlacement.DescribeDuration(chosen));
    }

    private void Arm(ChatLine line, Armed kind)
    {
        Disarm(line);
        line.Armed = kind;
        if (kind == Armed.Mute) line.Mute?.ShowConfirm(DoneBrush);
        else if (kind == Armed.Delete) line.Remove?.ShowConfirm(DoneBrush);
        else line.Ban?.ShowConfirm(ErrorBrush);
        line.DisarmTimer ??= new DispatcherTimer { Interval = BanConfirmWindow };
        line.DisarmTimer.Tick -= OnDisarmTick;
        line.DisarmTimer.Tick += OnDisarmTick;
        line.DisarmTimer.Tag = line;
        line.DisarmTimer.Stop();
        line.DisarmTimer.Start();
    }

    private static void OnDisarmTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer { Tag: ChatLine line }) Disarm(line);
    }

    private static void Disarm(ChatLine line)
    {
        line.DisarmTimer?.Stop();
        if (line.Armed == Armed.None) return;
        line.Armed = Armed.None;
        line.Mute?.ShowIdle();
        line.Ban?.ShowIdle();
        line.Remove?.ShowIdle();
    }

    private async Task ModerateAsync(ChatLine line, int? seconds, string doneText)
    {
        var chatter = line.Message.ChatterId;
        SetBusy(chatter, true);
        ModerationResult result;
        try
        {
            result = await _moderation.BanAsync(chatter, seconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Moderation failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        SetBusy(chatter, false);
        if (result.Success) MarkModerated(chatter, doneText);
        else ShowStatus(line, result.Message, ErrorBrush);
        Disarm(line);
        RestartIdle();
    }

    public event Action<string>? MessageDeleted;

    public void ShowDeleted(string messageId)
    {
        if (messageId.Length == 0 || !_deletedIds.Add(messageId)) return;
        foreach (var line in _lines)
        {
            if (line.Message.MessageId == messageId) MarkDeleted(line);
        }
    }

    private async Task DeleteMessageAsync(ChatLine line)
    {
        var chatter = line.Message.ChatterId;
        SetBusy(chatter, true);
        ModerationResult result;
        try
        {
            result = await _moderation.DeleteChatMessageAsync(line.Message.MessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Deleting a chat message failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        SetBusy(chatter, false);
        if (result.Success)
        {
            MarkDeleted(line);
            _deletedIds.Add(line.Message.MessageId);
            MessageDeleted?.Invoke(line.Message.MessageId);
        }
        else ShowStatus(line, result.Message, ErrorBrush);
        Disarm(line);
        RestartIdle();
    }

    private static void MarkDeleted(ChatLine line)
    {
        SetVisible(line.Remove, false);
        foreach (var inline in line.Text.Inlines.ToList())
        {
            if (!ReferenceEquals(inline, line.Status)) inline.TextDecorations = TextDecorations.Strikethrough;
        }
        line.Text.Opacity = 0.55;
        if (line.Removed != null) return;
        line.Removed = new Run("  · удалено") { Foreground = DoneBrush, FontSize = line.Text.FontSize * 0.9 };
        line.Text.Inlines.Add(line.Removed);
    }

    private async Task LiftAsync(ChatLine line)
    {
        var chatter = line.Message.ChatterId;
        SetBusy(chatter, true);
        ModerationResult result;
        try
        {
            result = await _moderation.UnbanAsync(chatter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Moderation failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        SetBusy(chatter, false);
        if (result.Success) MarkLifted(chatter);
        else ShowStatus(line, result.Message, ErrorBrush);
        RestartIdle();
    }

    private IEnumerable<ChatLine> LinesOf(string chatterId) => _lines.Where(l => l.Message.ChatterId == chatterId);

    private void SetBusy(string chatterId, bool busy)
    {
        foreach (var line in LinesOf(chatterId))
        {
            foreach (var button in new[] { line.Remove, line.Mute, line.Custom, line.Ban, line.Undo })
            {
                if (button != null) button.Button.IsEnabled = !busy;
            }
        }
    }

    private void MarkModerated(string chatterId, string text)
    {
        foreach (var line in LinesOf(chatterId))
        {
            ShowStatus(line, text, DoneBrush);
            SetVisible(line.Mute, false);
            SetVisible(line.Custom, false);
            SetVisible(line.Ban, false);
            SetVisible(line.Undo, true);
        }
    }

    private void MarkLifted(string chatterId)
    {
        foreach (var line in LinesOf(chatterId))
        {
            ClearStatus(line);
            SetVisible(line.Mute, true);
            SetVisible(line.Custom, true);
            SetVisible(line.Ban, true);
            SetVisible(line.Undo, false);
        }
    }

    private static void SetVisible(IconButton? button, bool visible)
    {
        if (button != null) button.Button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowStatus(ChatLine line, string text, Brush brush)
    {
        ClearStatus(line);
        line.Status = new Run("  · " + text) { Foreground = brush, FontSize = line.Text.FontSize * 0.9 };
        line.Text.Inlines.Add(line.Status);
    }

    private static void ClearStatus(ChatLine line)
    {
        if (line.Status == null) return;
        line.Text.Inlines.Remove(line.Status);
        line.Status = null;
    }
}
