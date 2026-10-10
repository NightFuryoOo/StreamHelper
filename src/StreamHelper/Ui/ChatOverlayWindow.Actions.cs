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
using System.Windows.Shapes;
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

    private const string MuteGlyph = "M2,6 H4.8 L8.6,2.8 V13.2 L4.8,10 H2 Z M11,5.8 L14.2,10.2 M14.2,5.8 L11,10.2";
    private const string ClockGlyph = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M8,5 V8.2 L10.4,9.8";
    private const string BanGlyph = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M3.8,3.8 L12.2,12.2";
    private const string UndoGlyph = "M5,4 L2.5,6.5 L5,9 M2.8,6.5 H9 A3.5,3.5 0 0 1 9,13.5 H6";
    private const string CheckGlyph = "M3,8.5 L6.4,12 L13,4.5";
    private const string TrashGlyph = "M3.5,5 H12.5 M6.5,5 V3.5 H9.5 V5 M4.8,5 L5.4,13 H10.6 L11.2,5";

    private enum Armed
    {
        None,
        Mute,
        Ban,
        Delete,
    }

    private sealed class IconButton
    {
        private readonly Path _glyph;
        private readonly TextBlock _label;
        private readonly string _idleGlyph;
        private readonly Brush _idleStroke;
        private string _name;

        public IconButton(string glyph, Brush stroke, Style style, string name)
        {
            _idleGlyph = glyph;
            _idleStroke = stroke;
            _name = name;
            _glyph = new Path
            {
                Data = Geometry.Parse(glyph),
                Stroke = stroke,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _label = new TextBlock
            {
                Text = "Точно?",
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_glyph);
            content.Children.Add(_label);
            Button = new Button { Content = content, Style = style, ToolTip = name };
            System.Windows.Automation.AutomationProperties.SetName(Button, name);
        }

        public Button Button { get; }

        public void Rename(string name)
        {
            _name = name;
            Button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(Button, name);
        }

        public void ShowConfirm(Brush brush)
        {
            _glyph.Data = Geometry.Parse(CheckGlyph);
            _glyph.Stroke = brush;
            _label.Foreground = brush;
            _label.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(Button, "Точно? " + _name);
        }

        public void ShowIdle()
        {
            _glyph.Data = Geometry.Parse(_idleGlyph);
            _glyph.Stroke = _idleStroke;
            _label.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(Button, _name);
        }
    }

    private string MuteName() => "Мут на " + ChatPlacement.DescribeDuration(ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes) * 60);

    private void AddActions(ChatLine line)
    {
        var style = (Style)FindResource("ChatAction");
        var mute = new IconButton(MuteGlyph, TextBrush, style, MuteName());
        var custom = new IconButton(ClockGlyph, TextBrush, style, "Мут на своё время");
        var ban = new IconButton(BanGlyph, ErrorBrush, style, "Бан");
        var undo = new IconButton(UndoGlyph, TextBrush, style, "Снять");
        undo.Button.Visibility = Visibility.Collapsed;
        var remove = new IconButton(TrashGlyph, TextBrush, style, "Удалить сообщение");
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
        if (result.Success) MarkDeleted(line);
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
