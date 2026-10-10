using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record EventSubFeature(string Label, string RequiredScope, IReadOnlyList<string> Types, string MissingScopeMessage, bool FollowsChannel = false);

public abstract class EventSubListener<T> : ISyncWorker where T : class
{
    private const int RecentIdLimit = 500;
    private static readonly TimeSpan DefaultKeepalive = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly SettingsStore _settings;
    private readonly IEventSubApi _api;
    private readonly string _url;
    private readonly EventSubFeature _feature;
    private readonly Func<IReadOnlyList<T>, Task> _deliver;
    private readonly object _gate = new();
    private readonly Queue<string> _recentOrder = new();
    private readonly HashSet<string> _recentIds = new();
    private CancellationTokenSource? _root;
    private CancellationTokenSource? _session;
    private Task? _loop;
    private int _failures;

    protected EventSubListener(
        SettingsStore settings, IEventSubApi api, string url, EventSubFeature feature, Func<IReadOnlyList<T>, Task> deliver)
    {
        _settings = settings;
        _api = api;
        _url = url;
        _feature = feature;
        _deliver = deliver;
    }

    internal TimeSpan KeepaliveGrace { get; set; } = TimeSpan.FromSeconds(3);
    internal TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(3);
    internal TimeSpan AuthRetryDelay { get; set; } = TimeSpan.FromSeconds(60);

    public SyncStatus Status { get; private set; } = new(SyncState.NotConnected, "Не подключено");

    public event Action<SyncStatus>? StatusChanged;

    protected abstract T? Select(EventSubMessage message);

    public void Start()
    {
        if (_loop != null) return;
        _root = new CancellationTokenSource();
        var token = _root.Token;
        _loop = Task.Run(() => RunAsync(token));
    }

    public void Stop()
    {
        _root?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _loop = null;
    }

    public void Restart()
    {
        lock (_gate)
        {
            try
            {
                _session?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) _session = session;
            try
            {
                var blocker = CheckReady();
                if (blocker != null)
                {
                    SetStatus(blocker);
                    await WaitAsync(IdleDelay, session.Token);
                    continue;
                }

                await RunConnectionAsync(session.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception) when (session.IsCancellationRequested)
            {
            }
            catch (AuthRequiredException ex)
            {
                SetStatus(SyncState.NeedsLogin, ex.Message);
                await WaitAsync(AuthRetryDelay, session.Token);
            }
            catch (Exception ex)
            {
                _failures++;
                Log.Write($"Twitch EventSub ({_feature.Label}) failed: " + ex.Message);
                SetStatus(SyncState.Error, ex is HttpRequestException or TimeoutException or IOException or WebSocketException
                    ? $"{_feature.Label}: " + ex.Message
                    : $"{_feature.Label}, ошибка: " + ex.Message);
                var seconds = Math.Min(BaseBackoff.TotalSeconds * Math.Pow(2, _failures - 1), MaxBackoff.TotalSeconds);
                await WaitAsync(TimeSpan.FromSeconds(seconds), session.Token);
            }
            finally
            {
                lock (_gate) _session = null;
                session.Dispose();
            }
        }
    }

    private SyncStatus? CheckReady()
    {
        var settings = _settings.Current;
        if (!settings.HasTwitchCredentials || !settings.HasTwitchTokens)
        {
            return new SyncStatus(SyncState.NotConnected, $"{_feature.Label}: Twitch не подключён. Подключи аккаунт в настройках, вкладка «Twitch».");
        }
        if (!settings.HasScope(_feature.RequiredScope))
        {
            return new SyncStatus(SyncState.NeedsLogin, _feature.MissingScopeMessage);
        }
        if (!_feature.FollowsChannel && !settings.IsOwnChannel)
        {
            return new SyncStatus(SyncState.NotConnected,
                $"{_feature.Label}: только на своём канале. Сейчас выбран канал {settings.ChannelLabel} (вкладка «Twitch»).");
        }
        return null;
    }

    private async Task RunConnectionAsync(CancellationToken ct)
    {
        ClientWebSocket? current = new();
        try
        {
            await current.ConnectAsync(new Uri(_url), ct);
            var keepalive = DefaultKeepalive;
            var subscribed = false;

            while (true)
            {
                var text = await ReceiveAsync(current, keepalive + KeepaliveGrace, ct);
                if (text == null) throw new IOException("Twitch закрыл соединение, переподключаюсь.");

                var message = EventSubParser.Parse(text);
                if (message == null) continue;

                switch (message.Type)
                {
                    case "session_welcome":
                        if (message.KeepaliveSeconds > 0) keepalive = TimeSpan.FromSeconds(message.KeepaliveSeconds);
                        if (!subscribed)
                        {
                            foreach (var type in _feature.Types)
                            {
                                await _api.CreateSubscriptionAsync(type, message.SessionId, ct);
                            }
                            subscribed = true;
                            _failures = 0;
                            var now = _settings.Current;
                            SetStatus(SyncState.Ok, _feature.FollowsChannel ? now.ConnectedText : $"Подключено · {now.TwitchLogin}");
                        }
                        break;

                    case "notification":
                        await HandleNotificationAsync(message);
                        break;

                    case "session_reconnect":
                        if (message.ReconnectUrl == null) throw new IOException("Twitch попросил переподключиться без адреса.");
                        var (next, nextKeepalive) = await OpenMigrationAsync(message.ReconnectUrl, ct);
                        var old = current;
                        current = next;
                        keepalive = nextKeepalive ?? keepalive;
                        old.Dispose();
                        break;

                    case "revocation":
                        throw new AuthRequiredException($"Twitch отозвал доступ к событиям ({_feature.Label.ToLowerInvariant()}): подключи Twitch заново.");
                }
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    private async Task<(ClientWebSocket Socket, TimeSpan? Keepalive)> OpenMigrationAsync(string url, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(new Uri(url), ct);
            while (true)
            {
                var text = await ReceiveAsync(socket, DefaultKeepalive + KeepaliveGrace, ct);
                if (text == null) throw new IOException("Twitch закрыл новое соединение до приветствия.");
                var message = EventSubParser.Parse(text);
                if (message?.Type != "session_welcome") continue;
                return (socket, message.KeepaliveSeconds > 0 ? TimeSpan.FromSeconds(message.KeepaliveSeconds) : null);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task HandleNotificationAsync(EventSubMessage message)
    {
        var item = Select(message);
        if (item == null) return;
        if (!Remember(message.MessageId)) return;
        try
        {
            await _deliver(new[] { item });
        }
        catch (Exception ex)
        {
            Log.Write($"Delivering an event ({_feature.Label}) failed: " + ex.Message);
        }
    }

    private bool Remember(string messageId)
    {
        if (messageId.Length == 0) return true;
        if (!_recentIds.Add(messageId)) return false;
        _recentOrder.Enqueue(messageId);
        while (_recentOrder.Count > RecentIdLimit) _recentIds.Remove(_recentOrder.Dequeue());
        return true;
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeoutCts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > 1_000_000) throw new IOException("Twitch прислал слишком большое сообщение.");
                if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("нет сообщений от Twitch дольше таймаута, переподключаюсь.");
        }
    }

    private static async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetStatus(SyncState state, string message) => SetStatus(new SyncStatus(state, message));

    private void SetStatus(SyncStatus next)
    {
        if (Status == next) return;
        Status = next;
        StatusChanged?.Invoke(next);
    }
}
