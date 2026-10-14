using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class ChatOverlayService : IPositionable
{
    private readonly SettingsStore _settings;
    private readonly ChatFeed _feed;
    private readonly ChatBadgeCatalog _badges;
    private readonly IModerationApi _moderation;
    private readonly IStreamApi _streams;
    private readonly ChatEmotes _emotes;
    private readonly IVoteApi _votes;
    private readonly VoteWatcher _watcher;
    private readonly IChatSendApi _chatSend;
    private readonly ChatterDirectory? _present;
    private readonly ChatViewers? _viewers;
    private readonly LinkPreviewCatalog? _previews;
    private readonly System.Windows.Threading.DispatcherTimer _viewerTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private ChatOverlayWindow? _window;
    private ChatHistoryWindow? _history;
    private IntPtr _historyReturnTo;
    private bool _positioning;

    public ChatOverlayService(
        SettingsStore settings, ChatFeed feed, ChatBadgeCatalog badges, IModerationApi moderation, IStreamApi streams, ChatEmotes emotes,
        IVoteApi votes, VoteWatcher watcher, IChatSendApi chatSend, IChattersApi? chatters = null, ILiveStreamApi? live = null,
        LinkPreviewCatalog? previews = null)
    {
        _chatSend = chatSend;
        _previews = previews;
        if (live != null)
        {
            var viewers = new ChatViewers(live, () => settings.Current);
            viewers.Changed += () => _window?.ShowViewers(viewers.Stream);
            _viewerTimer.Tick += (_, _) => _ = viewers.RefreshAsync();
            _viewers = viewers;
        }
        if (chatters != null) _present = new ChatterDirectory(chatters, () => settings.Current.HasTwitchTokens && settings.Current.HasChattersScope && !settings.Current.IsViewerChannel);
        _votes = votes;
        _watcher = watcher;
        watcher.Changed += OnVotesChanged;
        _streams = streams;
        _emotes = emotes;
        _settings = settings;
        _feed = feed;
        _badges = badges;
        _moderation = moderation;
        feed.Message += OnMessage;
        feed.Deleted += OnDeleted;
    }

    public bool IsPositioning => _positioning;

    public event Action<bool>? TypingChanged;

    public void BeginTyping()
    {
        if (_positioning || _settings.Current.ChatHidden || _window is not { IsVisible: true, IsFadingOut: false }) return;
        _window.BeginTyping();
    }

    public void Apply()
    {
        if (!_settings.Current.ChatHidden || _positioning)
        {
            var window = EnsureWindow();
            window.ApplyAppearance();
            window.Present();
            WatchViewers(true);
        }
        else
        {
            _window?.EndTyping(returnFocus: true);
            _window?.SetInteractive(false);
            _window?.FadeOut();
            WatchViewers(false);
        }
    }

    public void RefreshViewers() => _ = _viewers?.RefreshAsync();

    public void ReshowOverGame(IntPtr front) => _window?.ReshowOver(front);

    private void WatchViewers(bool shown)
    {
        if (_viewers == null) return;
        if (!shown)
        {
            _viewerTimer.Stop();
            return;
        }
        if (_viewerTimer.IsEnabled) return;
        _viewerTimer.Start();
        RefreshViewers();
    }

    public void ToggleVisible()
    {
        if (_positioning) return;
        var settings = _settings.Current;
        settings.ChatHidden = !settings.ChatHidden;
        _settings.Save();
        Apply();
    }

    public void ToggleInteractive()
    {
        if (_positioning || _window is not { IsVisible: true, IsFadingOut: false }) return;
        _window.SetInteractive(!_window.IsInteractive);
    }

    public void BeginPositioning()
    {
        _positioning = true;
        Apply();
        _window?.SetPositioning(true);
    }

    public void EndPositioning()
    {
        if (!_positioning) return;
        _positioning = false;
        if (_window != null)
        {
            _window.SetPositioning(false);
            _window.SaveGeometry();
        }
        Apply();
    }

    public void ResetPosition()
    {
        var settings = _settings.Current;
        settings.ChatLeft = null;
        settings.ChatTop = null;
        settings.ChatWidth = null;
        settings.ChatHeight = null;
        _settings.Save();
        _window?.MoveToConfiguredPlace();
    }

    public void ResetChannel()
    {
        _feed.Clear();
        _present?.Reset();
        _badges.Reset();
        _emotes.ResetChannel();
        _history?.Close();
        _viewers?.Clear();
        RefreshViewers();
        if (_window == null) return;
        _window.ResetChannel();
        _window.ShowVotes(null, null);
        _ = _badges.EnsureLoadedAsync();
        _ = _emotes.EnsureCatalogAsync();
    }

    public void Close()
    {
        _feed.Message -= OnMessage;
        _feed.Deleted -= OnDeleted;
        _watcher.Changed -= OnVotesChanged;
        _viewerTimer.Stop();
        _history?.Close();
        _window?.Close();
        _window = null;
    }

    private ChatOverlayWindow EnsureWindow()
    {
        if (_window != null) return _window;
        _ = _badges.EnsureLoadedAsync();
        _ = _emotes.EnsureCatalogAsync();
        var window = new ChatOverlayWindow(_settings, _badges, _moderation, _emotes, _votes, _watcher, _chatSend, _present, _previews);
        window.TypingChanged += typing => TypingChanged?.Invoke(typing);
        window.GeometryChanged += window.SaveGeometry;
        window.HistoryRequested += message => _ = OpenHistoryAsync(message);
        window.MessageDeleted += OnChatMessageDeleted;
        _window = window;
        foreach (var message in _feed.Recent())
        {
            if (_feed.IsDeleted(message.MessageId)) window.ShowDeleted(message.MessageId);
            window.AddMessage(message);
        }
        window.ShowVotes(_watcher.Poll, _watcher.Prediction);
        window.ShowViewers(_viewers?.Stream);
        return window;
    }

    public static ChatMessage HistorySample(ChatFeed feed, ChatPing ping) =>
        feed.LatestFrom(ping.ChatterId, ping.Login)
        ?? new ChatMessage(ping.Key, ping.ChatterId, ping.Login, ping.DisplayName, ping.Message, Array.Empty<ChatMention>(), ping.AtUtc);

    public Task OpenHistoryAsync(ChatPing ping) => OpenHistoryAsync(HistorySample(_feed, ping));

    public async Task OpenHistoryAsync(ChatMessage message)
    {
        DateTime? since = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            since = await _streams.GetStreamStartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Write("Stream start failed: " + ex.Message);
        }

        try
        {
            if (_history == null) _historyReturnTo = NativeMethods.GetForegroundWindow();
            else
            {
                var old = _history;
                _history = null;
                old.Closed -= OnHistoryClosed;
                old.Close();
            }
            var window = new ChatHistoryWindow(_feed, message, since, _emotes, _moderation, !_settings.Current.IsViewerChannel,
                _previews, () => _settings.Current.ChatLinkPreviews);
            window.Closed += OnHistoryClosed;
            _history = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Write("Chat history failed: " + ex.Message);
        }
    }

    private void OnChatMessageDeleted(string messageId) => _feed.MarkDeleted(messageId);

    private void OnDeleted(IReadOnlyList<string> ids)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.InvokeAsync(() =>
        {
            foreach (var id in ids)
            {
                _window?.ShowDeleted(id);
                _history?.ShowDeleted(id);
            }
        });
    }

    private void OnHistoryClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _history)) return;
        _history = null;
        var back = _historyReturnTo;
        if (back != IntPtr.Zero && NativeMethods.Exists(back)) NativeMethods.SetForegroundWindow(back);
    }

    private void OnVotesChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.InvokeAsync(() => _window?.ShowVotes(_watcher.Poll, _watcher.Prediction));
    }

    private void OnMessage(ChatMessage message)
    {
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.InvokeAsync(() => _window?.AddMessage(message));
    }
}
