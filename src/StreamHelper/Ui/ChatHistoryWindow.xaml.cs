using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class ChatHistoryWindow : Window
{
    private static readonly Brush TimeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA0)));

    private const double TextSize = 14;
    private static readonly TimeSpan EmoteWait = TimeSpan.FromSeconds(6);

    private readonly ChatFeed _feed;
    private readonly ChatEmotes _emotes;
    private readonly EmoteAnimator _animator = new(200);
    private readonly List<ChatMessage> _messages = new();
    private readonly string _key;
    private readonly DateTime? _sinceUtc;
    private readonly HashSet<string> _seen = new();
    private int _count;

    public ChatHistoryWindow(ChatFeed feed, ChatMessage sample, DateTime? sinceUtc, ChatEmotes emotes)
    {
        InitializeComponent();
        _feed = feed;
        _emotes = emotes;
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
        Box.Document.Blocks.Add(BuildParagraph(message));
        _count++;
        return true;
    }

    private Paragraph BuildParagraph(ChatMessage message)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
        paragraph.Inlines.Add(new Run(message.AtUtc.ToLocalTime().ToString("HH:mm:ss") + "  ") { Foreground = TimeBrush, FontSize = 12 });
        foreach (var inline in _emotes.Inlines(message, TextSize, _animator)) paragraph.Inlines.Add(inline);
        return paragraph;
    }

    private async Task LoadEmotesAsync()
    {
        if (_emotes.Missing(_messages).Count == 0) return;
        await _emotes.PreloadAsync(_messages.ToList(), EmoteWait);
        var atEnd = Box.VerticalOffset + Box.ViewportHeight >= Box.ExtentHeight - 4;
        var offset = Box.VerticalOffset;
        _animator.Clear();
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

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
