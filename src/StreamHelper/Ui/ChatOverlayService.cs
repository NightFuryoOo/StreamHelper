using System;
using System.Threading;
using System.Windows;
using StreamHelper.Api;
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
    private ChatOverlayWindow? _window;
    private ChatHistoryWindow? _history;
    private IntPtr _historyReturnTo;
    private bool _positioning;

    public ChatOverlayService(
        SettingsStore settings, ChatFeed feed, ChatBadgeCatalog badges, IModerationApi moderation, IStreamApi streams, ChatEmotes emotes,
        IVoteApi votes, VoteWatcher watcher)
    {
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
    }

    public bool IsPositioning => _positioning;

    public void Apply()
    {
        if (!_settings.Current.ChatHidden || _positioning)
        {
            var window = EnsureWindow();
            window.ApplyAppearance();
            window.Present();
        }
        else
        {
            _window?.SetInteractive(false);
            _window?.FadeOut();
        }
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
        _badges.Reset();
        _emotes.ResetChannel();
        _history?.Close();
        if (_window == null) return;
        _window.ResetChannel();
        _window.ShowVotes(null, null);
        _ = _badges.EnsureLoadedAsync();
        _ = _emotes.EnsureCatalogAsync();
    }

    public void Close()
    {
        _feed.Message -= OnMessage;
        _watcher.Changed -= OnVotesChanged;
        _history?.Close();
        _window?.Close();
        _window = null;
    }

    private ChatOverlayWindow EnsureWindow()
    {
        if (_window != null) return _window;
        _ = _badges.EnsureLoadedAsync();
        _ = _emotes.EnsureCatalogAsync();
        var window = new ChatOverlayWindow(_settings, _badges, _moderation, _emotes, _votes, _watcher);
        window.GeometryChanged += window.SaveGeometry;
        window.HistoryRequested += OnHistoryRequested;
        _window = window;
        foreach (var message in _feed.Recent()) window.AddMessage(message);
        window.ShowVotes(_watcher.Poll, _watcher.Prediction);
        return window;
    }

    private async void OnHistoryRequested(ChatMessage message)
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

        if (_history == null) _historyReturnTo = NativeMethods.GetForegroundWindow();
        else
        {
            var old = _history;
            _history = null;
            old.Closed -= OnHistoryClosed;
            old.Close();
        }
        var window = new ChatHistoryWindow(_feed, message, since, _emotes);
        window.Closed += OnHistoryClosed;
        _history = window;
        window.Show();
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
