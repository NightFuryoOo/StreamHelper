using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatHistoryWindow : Window
{
    private static readonly Brush TimeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA0)));
    private static readonly Brush IdleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEE)));
    private static readonly Brush DoneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeleteLimit = TimeSpan.FromHours(6);
    private const string DeleteName = "Удалить сообщение";
    private static ControlTemplate? _trashTemplate;
    private static Style? _trashStyle;

    private sealed record Trash(Paragraph Paragraph, IconButton Icon);

    private const double TextSize = 14;
    private static readonly TimeSpan EmoteWait = TimeSpan.FromSeconds(6);

    private readonly ChatFeed _feed;
    private readonly ChatEmotes _emotes;
    private readonly IModerationApi _moderation;
    private readonly EmoteAnimator _animator = new(200);
    private readonly List<ChatMessage> _messages = new();
    private readonly string _key;
    private readonly DateTime? _sinceUtc;
    private readonly HashSet<string> _seen = new();
    private readonly Dictionary<string, ChatMessage> _byId = new();
    private readonly Dictionary<string, Paragraph> _paragraphs = new();
    private readonly Dictionary<string, Trash> _trash = new();
    private readonly Dictionary<string, string> _errors = new();
    private readonly HashSet<string> _busy = new();
    private readonly DispatcherTimer _disarm = new() { Interval = ConfirmWindow };
    private string? _armed;
    private readonly bool _canDelete;
    private int _count;

    public ChatHistoryWindow(ChatFeed feed, ChatMessage sample, DateTime? sinceUtc, ChatEmotes emotes, IModerationApi moderation, bool canDelete = true)
    {
        InitializeComponent();
        _canDelete = canDelete;
        _feed = feed;
        _emotes = emotes;
        _moderation = moderation;
        _disarm.Tick += (_, _) => Disarm();
        _key = ChatFeed.KeyOf(sample);
        _sinceUtc = sinceUtc;

        var name = sample.ChatterName.Length > 0 ? sample.ChatterName : sample.ChatterLogin;
        var (r, g, b) = ChatColors.Readable(sample.Color, sample.ChatterLogin.Length > 0 ? sample.ChatterLogin : name);
        NickText.Text = name;
        NickText.Foreground = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
        Title = "История: " + name;
        Box.Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = TextSize };

        _feed.Message += OnLive;
        Closed += (_, _) =>
        {
            _feed.Message -= OnLive;
            _animator.Stop();
            _disarm.Stop();
        };
        foreach (var message in _feed.HistoryOf(_key, _sinceUtc)) Append(message);
        UpdateCount();

        Loaded += (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Box.ScrollToEnd();
            _ = LoadEmotesAsync();
        };
    }

    public string ChatterKey => _key;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnLive(ChatMessage message)
    {
        if (ChatFeed.KeyOf(message) != _key) return;
        if (_sinceUtc != null && message.AtUtc < _sinceUtc) return;
        Dispatcher.InvokeAsync(async () =>
        {
            await _emotes.PreloadAsync(new[] { message }, EmoteWait);
            var atEnd = Box.VerticalOffset + Box.ViewportHeight >= Box.ExtentHeight - 4;
            if (!Append(message)) return;
            UpdateCount();
            if (atEnd) Box.ScrollToEnd();
        });
    }

    private bool Append(ChatMessage message)
    {
        if (message.MessageId.Length > 0 && !_seen.Add(message.MessageId)) return false;
        _messages.Add(message);
        if (message.MessageId.Length > 0) _byId[message.MessageId] = message;
        Box.Document.Blocks.Add(BuildParagraph(message));
        _count++;
        return true;
    }

    public void ShowDeleted(string messageId)
    {
        if (_byId.TryGetValue(messageId, out var message)) Refresh(message);
    }

    private static string ReplyPreview(string text) => text.Length > 120 ? text[..120].TrimEnd() + "…" : text;

    private bool CanDelete(ChatMessage message) =>
        _canDelete && message.MessageId.Length > 0 && DateTime.UtcNow - message.AtUtc < DeleteLimit;

    private Paragraph BuildParagraph(ChatMessage message)
    {
        var id = message.MessageId;
        var deleted = _feed.IsDeleted(id);
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
        if (message.Reply is { } parent)
        {
            paragraph.Inlines.Add(new Run("↪ Ответ для ") { Foreground = TimeBrush, FontSize = 12 });
            paragraph.Inlines.Add(new Run(parent.ParentLabel) { Foreground = TimeBrush, FontSize = 12, FontWeight = FontWeights.SemiBold });
            if (parent.ParentText.Length > 0) paragraph.Inlines.Add(new Run(": " + ReplyPreview(parent.ParentText)) { Foreground = TimeBrush, FontSize = 12 });
            paragraph.Inlines.Add(new LineBreak());
        }
        paragraph.Inlines.Add(new Run(message.AtUtc.ToLocalTime().ToString("HH:mm:ss") + "  ") { Foreground = TimeBrush, FontSize = 12 });
        foreach (var inline in _emotes.Inlines(message, TextSize, _animator))
        {
            if (deleted)
            {
                inline.TextDecorations = TextDecorations.Strikethrough;
                if (inline is InlineUIContainer { Child: { } picture }) picture.Opacity = 0.55;
                else inline.Foreground = TimeBrush;
            }
            paragraph.Inlines.Add(inline);
        }
        _trash.Remove(id);
        if (deleted) paragraph.Inlines.Add(new Run("  · удалено") { Foreground = DoneBrush, FontSize = 12 });
        else if (CanDelete(message)) AddTrash(paragraph, message);
        if (_errors.TryGetValue(id, out var error)) paragraph.Inlines.Add(new Run("  " + error) { Foreground = ErrorBrush, FontSize = 12 });
        if (id.Length > 0) _paragraphs[id] = paragraph;
        return paragraph;
    }

    private void AddTrash(Paragraph paragraph, ChatMessage message)
    {
        var id = message.MessageId;
        var icon = new IconButton(Glyphs.Trash, IdleBrush, TrashStyle(), DeleteName);
        var button = icon.Button;
        button.Margin = new Thickness(6, 0, 0, 0);
        button.Visibility = Visibility.Hidden;
        button.IsEnabled = !_busy.Contains(id);
        button.Click += async (_, _) => await OnTrashClickAsync(message);
        paragraph.Inlines.Add(new InlineUIContainer(button) { BaselineAlignment = BaselineAlignment.Center });
        paragraph.MouseEnter += (_, _) => button.Visibility = Visibility.Visible;
        paragraph.MouseLeave += (_, _) =>
        {
            if (_armed == id) Disarm();
            button.Visibility = Visibility.Hidden;
        };
        var trash = new Trash(paragraph, icon);
        _trash[id] = trash;
        if (_armed == id) ShowArmed(trash, true);
    }

    private static void ShowArmed(Trash trash, bool armed)
    {
        if (!armed)
        {
            trash.Icon.ShowIdle();
            return;
        }
        trash.Icon.ShowConfirm(DoneBrush);
        trash.Icon.Button.Visibility = Visibility.Visible;
    }

    private void Arm(string id)
    {
        Disarm();
        if (!_trash.TryGetValue(id, out var trash)) return;
        _armed = id;
        ShowArmed(trash, true);
        _disarm.Stop();
        _disarm.Start();
    }

    private void Disarm()
    {
        _disarm.Stop();
        if (_armed is not { } id) return;
        _armed = null;
        if (_trash.TryGetValue(id, out var trash)) ShowArmed(trash, false);
    }

    private async Task OnTrashClickAsync(ChatMessage message)
    {
        var id = message.MessageId;
        if (_busy.Contains(id)) return;
        if (_armed != id)
        {
            Arm(id);
            return;
        }
        Disarm();
        _busy.Add(id);
        _errors.Remove(id);
        if (_trash.TryGetValue(id, out var trash)) trash.Icon.Button.IsEnabled = false;
        ModerationResult result;
        try
        {
            result = await _moderation.DeleteChatMessageAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Deleting a chat message failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        _busy.Remove(id);
        if (result.Success) _feed.MarkDeleted(id);
        else _errors[id] = result.Message;
        Refresh(message);
    }

    private void Refresh(ChatMessage message)
    {
        if (!_paragraphs.TryGetValue(message.MessageId, out var old) || !ReferenceEquals(old.Parent, Box.Document)) return;
        var fresh = BuildParagraph(message);
        Box.Document.Blocks.InsertAfter(old, fresh);
        Box.Document.Blocks.Remove(old);
        if (fresh.IsMouseOver && _trash.TryGetValue(message.MessageId, out var trash)) trash.Icon.Button.Visibility = Visibility.Visible;
    }

    private static Style TrashStyle()
    {
        if (_trashStyle != null) return _trashStyle;
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, TrashTemplate()));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FocusableProperty, false));
        style.Seal();
        _trashStyle = style;
        return style;
    }

    private static ControlTemplate TrashTemplate()
    {
        if (_trashTemplate != null) return _trashTemplate;
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new Thickness(3, 1, 3, 1));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "bd"));
        template.Triggers.Add(hover);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "bd"));
        template.Triggers.Add(disabled);
        template.Seal();
        _trashTemplate = template;
        return template;
    }

    private async Task LoadEmotesAsync()
    {
        if (_emotes.Missing(_messages).Count == 0) return;
        await _emotes.PreloadAsync(_messages.ToList(), EmoteWait);
        var atEnd = Box.VerticalOffset + Box.ViewportHeight >= Box.ExtentHeight - 4;
        var offset = Box.VerticalOffset;
        _animator.Clear();
        _paragraphs.Clear();
        _trash.Clear();
        Box.Document.Blocks.Clear();
        foreach (var message in _messages) Box.Document.Blocks.Add(BuildParagraph(message));
        Box.UpdateLayout();
        if (atEnd) Box.ScrollToEnd();
        else Box.ScrollToVerticalOffset(offset);
    }

    private void UpdateCount()
    {
        var text = "Сообщений: " + _count;
        if (_sinceUtc is { } since) text += " · стрим с " + since.ToLocalTime().ToString("HH:mm");
        CountText.Text = text;
    }

}
