using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal sealed class MockEventSubServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<WebSocket> _sockets = new();
    private readonly Task _loop;
    private int _connections;

    public MockEventSubServer()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoop);
    }

    public int Port { get; }

    public string Url(string path = "/ws") => $"ws://127.0.0.1:{Port}{path}";

    public int ConnectionCount => Volatile.Read(ref _connections);

    public Func<int, string, WebSocket, Task> OnConnection { get; set; } = (_, _, _) => Task.CompletedTask;

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var path = context.Request.Url!.AbsolutePath;
            var wsContext = await context.AcceptWebSocketAsync(null);
            var index = Interlocked.Increment(ref _connections);
            lock (_sockets) _sockets.Add(wsContext.WebSocket);
            _ = Task.Run(async () =>
            {
                try
                {
                    await OnConnection(index, path, wsContext.WebSocket);
                }
                catch
                {
                }
            });
        }
    }

    public static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    public static string Welcome(string sessionId = "SESSION1", int keepalive = 10) =>
        "{\"metadata\":{\"message_id\":\"welcome-" + sessionId + "\",\"message_type\":\"session_welcome\",\"message_timestamp\":\"2026-10-05T12:00:00.123456789Z\"}," +
        "\"payload\":{\"session\":{\"id\":\"" + sessionId + "\",\"status\":\"connected\",\"keepalive_timeout_seconds\":" + keepalive + ",\"reconnect_url\":null}}}";

    public static string Keepalive() =>
        "{\"metadata\":{\"message_id\":\"ka-" + Guid.NewGuid().ToString("N") + "\",\"message_type\":\"session_keepalive\",\"message_timestamp\":\"2026-10-05T12:00:01Z\"},\"payload\":{}}";

    public static string Reconnect(string url) =>
        "{\"metadata\":{\"message_id\":\"rc-1\",\"message_type\":\"session_reconnect\",\"message_timestamp\":\"2026-10-05T12:00:02Z\"}," +
        "\"payload\":{\"session\":{\"id\":\"SESSION1\",\"status\":\"reconnecting\",\"keepalive_timeout_seconds\":null,\"reconnect_url\":\"" + url + "\"}}}";

    public static string Revocation() =>
        "{\"metadata\":{\"message_id\":\"rv-1\",\"message_type\":\"revocation\",\"message_timestamp\":\"2026-10-05T12:00:03Z\",\"subscription_type\":\"channel.subscribe\",\"subscription_version\":\"1\"}," +
        "\"payload\":{\"subscription\":{\"id\":\"s1\",\"status\":\"authorization_revoked\",\"type\":\"channel.subscribe\"}}}";

    public static string Notification(string messageId, string subscriptionType, string eventJson) =>
        "{\"metadata\":{\"message_id\":\"" + messageId + "\",\"message_type\":\"notification\",\"message_timestamp\":\"2026-10-05T12:05:00.987654321Z\"," +
        "\"subscription_type\":\"" + subscriptionType + "\",\"subscription_version\":\"1\"}," +
        "\"payload\":{\"subscription\":{\"id\":\"s1\",\"type\":\"" + subscriptionType + "\",\"version\":\"1\",\"status\":\"enabled\"},\"event\":" + eventJson + "}}";

    public static string NewSubEvent(string login, string name, string tier = "1000", bool isGift = false) =>
        $"{{\"user_id\":\"1\",\"user_login\":\"{login}\",\"user_name\":\"{name}\",\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\",\"tier\":\"{tier}\",\"is_gift\":{(isGift ? "true" : "false")}}}";

    public async Task CloseAllAsync()
    {
        WebSocket[] sockets;
        lock (_sockets) sockets = _sockets.ToArray();
        foreach (var socket in sockets)
        {
            try
            {
                if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch
        {
        }
    }
}

internal sealed class FakeEventSubApi : IEventSubApi
{
    public List<(string Type, string SessionId)> Calls { get; } = new();

    public Exception? Throw { get; set; }

    public Task CreateSubscriptionAsync(string type, string sessionId, CancellationToken ct)
    {
        lock (Calls) Calls.Add((type, sessionId));
        return Throw != null ? Task.FromException(Throw) : Task.CompletedTask;
    }

    public int CallCount
    {
        get
        {
            lock (Calls) return Calls.Count;
        }
    }
}

internal static class Wait
{
    public static async Task<bool> Until(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }
}

public class EventSubParserTests
{
    [Fact]
    public void Parses_the_welcome_message()
    {
        var message = EventSubParser.Parse(MockEventSubServer.Welcome("ABC", 15))!;

        Assert.Equal("session_welcome", message.Type);
        Assert.Equal("ABC", message.SessionId);
        Assert.Equal(15, message.KeepaliveSeconds);
        Assert.Null(message.ReconnectUrl);
        Assert.Null(message.Subscriber);
    }

    [Fact]
    public void Parses_the_reconnect_message_with_a_null_keepalive()
    {
        var message = EventSubParser.Parse(MockEventSubServer.Reconnect("wss://example.test/ws?x=1"))!;

        Assert.Equal("session_reconnect", message.Type);
        Assert.Equal("wss://example.test/ws?x=1", message.ReconnectUrl);
        Assert.Equal(0, message.KeepaliveSeconds);
    }

    [Fact]
    public void A_new_subscription_becomes_a_subscriber_keyed_by_message_id_with_a_utc_timestamp()
    {
        var json = MockEventSubServer.Notification("msg-1", "channel.subscribe", MockEventSubServer.NewSubEvent("alice", "Alice", "2000"));

        var message = EventSubParser.Parse(json)!;

        var sub = message.Subscriber!;
        Assert.Equal("msg-1", sub.Key);
        Assert.Equal(SubscriptionKind.New, sub.Kind);
        Assert.Equal("Alice", sub.Name);
        Assert.Equal("Tier 2", sub.TierText);
        Assert.Equal(new DateTime(2026, 10, 5, 12, 5, 0, DateTimeKind.Utc).AddTicks(9876543), sub.AtUtc);
        Assert.Equal("новая подписка", sub.KindText);
    }

    [Fact]
    public void A_gifted_recipient_event_is_skipped_because_the_gift_event_covers_it()
    {
        var json = MockEventSubServer.Notification("msg-2", "channel.subscribe", MockEventSubServer.NewSubEvent("bob", "Bob", "1000", isGift: true));

        Assert.Null(EventSubParser.Parse(json)!.Subscriber);
    }

    [Fact]
    public void A_resub_carries_months_streak_and_the_message()
    {
        var ev = "{\"user_login\":\"carol\",\"user_name\":\"Carol\",\"tier\":\"1000\",\"message\":{\"text\":\"Спасибо за стримы!\",\"emotes\":[]},\"cumulative_months\":7,\"streak_months\":4,\"duration_months\":1}";

        var sub = EventSubParser.Parse(MockEventSubServer.Notification("msg-3", "channel.subscription.message", ev))!.Subscriber!;

        Assert.Equal(SubscriptionKind.Resub, sub.Kind);
        Assert.Equal(7, sub.Months);
        Assert.Equal(4, sub.StreakMonths);
        Assert.Equal("Спасибо за стримы!", sub.Message);
        Assert.True(sub.HasMessage);
        Assert.Equal("Tier 1 · 7 мес., подряд 4", sub.DetailText);
    }

    [Fact]
    public void A_resub_with_a_null_streak_and_no_message_is_handled()
    {
        var ev = "{\"user_login\":\"dan\",\"user_name\":\"Dan\",\"tier\":\"3000\",\"message\":{\"text\":\"\",\"emotes\":null},\"cumulative_months\":2,\"streak_months\":null,\"duration_months\":1}";

        var sub = EventSubParser.Parse(MockEventSubServer.Notification("msg-4", "channel.subscription.message", ev))!.Subscriber!;

        Assert.Equal(0, sub.StreakMonths);
        Assert.False(sub.HasMessage);
        Assert.Equal("Tier 3 · 2 мес.", sub.DetailText);
    }

    [Fact]
    public void A_gift_bundle_keeps_the_total_and_an_anonymous_gifter_is_named_neutrally()
    {
        var ev = "{\"user_id\":null,\"user_login\":null,\"user_name\":null,\"tier\":\"1000\",\"total\":10,\"cumulative_total\":null,\"is_anonymous\":true}";

        var sub = EventSubParser.Parse(MockEventSubServer.Notification("msg-5", "channel.subscription.gift", ev))!.Subscriber!;

        Assert.Equal(SubscriptionKind.Gift, sub.Kind);
        Assert.Equal(10, sub.GiftTotal);
        Assert.True(sub.IsAnonymous);
        Assert.Equal("Аноним", sub.Name);
        Assert.Equal("Tier 1 · 10 шт.", sub.DetailText);
        Assert.Equal("Подарочные подписки", sub.ToastTitle);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"payload\":{}}")]
    public void Garbage_is_ignored(string text) => Assert.Null(EventSubParser.Parse(text));

    [Fact]
    public void An_unknown_notification_type_yields_no_subscriber()
    {
        var json = MockEventSubServer.Notification("m", "channel.follow", "{\"user_login\":\"x\"}");

        Assert.Null(EventSubParser.Parse(json)!.Subscriber);
    }

    [Fact]
    public void A_subscriber_survives_a_store_round_trip_with_its_kind()
    {
        using var dir = new TempDir();
        var path = dir.File("subscribers.json");
        var store = new SubscriberStore(path);
        var ev = "{\"user_login\":\"carol\",\"user_name\":\"Carol\",\"tier\":\"1000\",\"message\":{\"text\":\"hi\"},\"cumulative_months\":7,\"streak_months\":1}";
        store.AddRange(new[]
        {
            EventSubParser.Parse(MockEventSubServer.Notification("a", "channel.subscription.message", ev))!.Subscriber!,
            EventSubParser.Parse(MockEventSubServer.Notification("b", "channel.subscribe", MockEventSubServer.NewSubEvent("x", "X")))!.Subscriber!,
        });
        store.Items[0].Seen = true;

        var reloaded = new SubscriberStore(path);

        Assert.Equal(2, reloaded.Items.Count);
        Assert.Equal(new[] { "b", "a" }, reloaded.Items.Select(s => s.Key).ToArray());
        Assert.Equal(SubscriptionKind.New, reloaded.Items[0].Kind);
        Assert.Equal(SubscriptionKind.Resub, reloaded.Items[1].Kind);
        Assert.Equal("hi", reloaded.Items[1].Message);
        Assert.True(reloaded.Items[0].Seen);
        Assert.Contains("\"Resub\"", File.ReadAllText(path));
        Assert.Equal(1, reloaded.UnseenCount);
    }

    [Fact]
    public void The_store_ignores_a_repeated_message_id()
    {
        using var dir = new TempDir();
        var store = new SubscriberStore(dir.File("subscribers.json"));
        var sub = EventSubParser.Parse(MockEventSubServer.Notification("same", "channel.subscribe", MockEventSubServer.NewSubEvent("x", "X")))!.Subscriber!;

        store.AddRange(new[] { sub });
        var again = store.AddRange(new[] { EventSubParser.Parse(MockEventSubServer.Notification("same", "channel.subscribe", MockEventSubServer.NewSubEvent("x", "X")))!.Subscriber! });

        Assert.Empty(again);
        Assert.Single(store.Items);
    }
}

public class SubscriptionListenerTests
{
    private static SettingsStore ReadySettings(TempDir dir, string scopes = "moderator:read:followers channel:read:subscriptions")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private static SubscriptionListener NewListener(SettingsStore settings, FakeEventSubApi api, string url, List<Subscriber> delivered)
    {
        var listener = new SubscriptionListener(settings, api, url, items =>
        {
            lock (delivered) delivered.AddRange(items);
            return Task.CompletedTask;
        })
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
            AuthRetryDelay = TimeSpan.FromMilliseconds(400),
        };
        return listener;
    }

    private static int Count(List<Subscriber> list)
    {
        lock (list) return list.Count;
    }

    [Fact]
    public async Task Subscribes_to_all_three_types_with_the_session_id_and_delivers_events_once()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-A"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", "channel.subscribe", MockEventSubServer.NewSubEvent("alice", "Alice")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", "channel.subscribe", MockEventSubServer.NewSubEvent("alice", "Alice")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", "channel.subscribe", MockEventSubServer.NewSubEvent("bob", "Bob", isGift: true)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", "channel.subscription.gift",
                "{\"user_login\":\"gina\",\"user_name\":\"Gina\",\"tier\":\"1000\",\"total\":5,\"is_anonymous\":false}"));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = ReadySettings(dir);
        var api = new FakeEventSubApi();
        var delivered = new List<Subscriber>();
        var listener = NewListener(settings, api, server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) >= 2));
            await Task.Delay(300);

            Assert.Equal(SubscriptionListener.SubscriptionTypes, api.Calls.Select(c => c.Type).ToArray());
            Assert.All(api.Calls, c => Assert.Equal("SESS-A", c.SessionId));
            lock (delivered)
            {
                Assert.Equal(new[] { "n1", "n3" }, delivered.Select(d => d.Key).ToArray());
                Assert.Equal(SubscriptionKind.Gift, delivered[1].Kind);
            }
            Assert.Equal(SyncState.Ok, listener.Status.State);
            Assert.Equal("Подключено · streamer", listener.Status.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Follows_a_reconnect_request_without_resubscribing_and_keeps_receiving()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (index, path, socket) =>
        {
            if (path == "/ws")
            {
                await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-A"));
                await Wait.Until(() => false, 300);
                await MockEventSubServer.SendAsync(socket, MockEventSubServer.Reconnect(server.Url("/ws-next")));
                await Task.Delay(Timeout.Infinite);
            }
            else
            {
                await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-A"));
                await Wait.Until(() => false, 200);
                await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("after", "channel.subscribe", MockEventSubServer.NewSubEvent("eve", "Eve")));
                await Task.Delay(Timeout.Infinite);
            }
        };
        var api = new FakeEventSubApi();
        var delivered = new List<Subscriber>();
        var listener = NewListener(ReadySettings(dir), api, server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) == 1));

            Assert.Equal(3, api.CallCount);
            Assert.Equal(2, server.ConnectionCount);
            Assert.Equal(SyncState.Ok, listener.Status.State);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnects_and_resubscribes_when_the_server_goes_silent_past_the_keepalive()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (index, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-" + index, keepalive: 1));
            if (index == 2)
            {
                for (var i = 0; i < 20; i++)
                {
                    await MockEventSubServer.SendAsync(socket, MockEventSubServer.Keepalive());
                    await Task.Delay(300);
                }
            }
            else
            {
                await Task.Delay(Timeout.Infinite);
            }
        };
        var api = new FakeEventSubApi();
        var listener = NewListener(ReadySettings(dir), api, server.Url(), new List<Subscriber>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => api.CallCount >= 6, 10000));

            Assert.Equal("SESS-1", api.Calls[0].SessionId);
            Assert.Equal("SESS-2", api.Calls[3].SessionId);
            Assert.Equal(SyncState.Ok, listener.Status.State);

            await Task.Delay(1500);
            Assert.Equal(6, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Reconnects_after_the_server_closes_the_socket()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (index, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-" + index));
            if (index == 1)
            {
                await Wait.Until(() => false, 300);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "restart", CancellationToken.None);
            }
            else
            {
                await Task.Delay(Timeout.Infinite);
            }
        };
        var api = new FakeEventSubApi();
        var listener = NewListener(ReadySettings(dir), api, server.Url(), new List<Subscriber>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => api.CallCount >= 6));
            Assert.Equal("SESS-2", api.Calls[3].SessionId);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_revocation_asks_for_a_new_login_and_a_restart_reconnects()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (index, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-" + index));
            if (index == 1)
            {
                await Wait.Until(() => false, 200);
                await MockEventSubServer.SendAsync(socket, MockEventSubServer.Revocation());
            }
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi();
        var listener = NewListener(ReadySettings(dir), api, server.Url(), new List<Subscriber>());
        listener.AuthRetryDelay = TimeSpan.FromMinutes(5);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NeedsLogin));
            Assert.Contains("отозвал", listener.Status.Message);
            Assert.Equal(1, server.ConnectionCount);

            listener.Restart();

            Assert.True(await Wait.Until(() => server.ConnectionCount == 2 && listener.Status.State == SyncState.Ok));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Missing_permission_from_the_api_is_reported_as_needing_a_new_login()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome());
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi { Throw = new AuthRequiredException("нужно новое право") };
        var listener = NewListener(ReadySettings(dir), api, server.Url(), new List<Subscriber>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NeedsLogin));
            Assert.Equal("нужно новое право", listener.Status.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_server_that_is_down_is_retried_with_an_error_status()
    {
        using var dir = new TempDir();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var listener = NewListener(ReadySettings(dir), new FakeEventSubApi(), $"ws://127.0.0.1:{deadPort}/ws", new List<Subscriber>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.Error));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Without_a_login_or_the_subscription_right_it_does_not_connect_and_says_why()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var api = new FakeEventSubApi();
        var settings = new SettingsStore(dir.File("settings.json"));
        var listener = NewListener(settings, api, server.Url(), new List<Subscriber>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NotConnected));

            var old = ReadySettings(dir, "moderator:read:followers");
            settings.Current.TwitchClientId = old.Current.TwitchClientId;
            settings.Current.TwitchAccessToken = "x";
            settings.Current.TwitchRefreshToken = "y";
            settings.Current.TwitchUserId = "777";
            settings.Current.TwitchScopes = "moderator:read:followers";
            listener.Restart();

            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NeedsLogin));
            Assert.Contains("переподключи", listener.Status.Message);
            Assert.Equal(0, server.ConnectionCount);
            Assert.Equal(0, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Connecting_after_login_starts_listening_without_restarting_the_app()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome());
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi();
        var settings = new SettingsStore(dir.File("settings.json"));
        var listener = NewListener(settings, api, server.Url(), new List<Subscriber>());
        listener.IdleDelay = TimeSpan.FromMinutes(5);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NotConnected));

            settings.Current.TwitchClientId = "c";
            settings.Current.TwitchAccessToken = "x";
            settings.Current.TwitchRefreshToken = "y";
            settings.Current.TwitchUserId = "777";
            settings.Current.TwitchScopes = "moderator:read:followers channel:read:subscriptions";
            listener.Restart();

            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.Ok));
            Assert.Equal(3, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class TwitchSubscriptionApiTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "moderator:read:followers channel:read:subscriptions";
        return settings;
    }

    [Fact]
    public async Task Posts_a_websocket_subscription_for_the_channel_with_the_bearer_token()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            Assert.Equal("POST", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.Equal("twitch-client", req.Headers["Client-Id"]);
            Assert.StartsWith("application/json", req.ContentType);
            return (202, "{\"data\":[{\"id\":\"s1\"}]}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync("channel.subscribe", "SESS-1", CancellationToken.None);

        var request = Assert.Single(da.Requests);
        Assert.Contains("POST /helix/eventsub/subscriptions", request);
        Assert.Contains("\"type\":\"channel.subscribe\"", request);
        Assert.Contains("\"version\":\"1\"", request);
        Assert.Contains("\"broadcaster_user_id\":\"777\"", request);
        Assert.Contains("\"method\":\"websocket\"", request);
        Assert.Contains("\"session_id\":\"SESS-1\"", request);
    }

    [Fact]
    public async Task The_chat_subscription_names_the_signed_in_user_too_and_the_others_do_not()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (202, "{}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync("channel.chat.message", "SESS-C", CancellationToken.None);
        await client.CreateSubscriptionAsync("channel.subscribe", "SESS-C", CancellationToken.None);

        var chat = da.Requests[0];
        var other = da.Requests[1];
        Assert.Contains("\"type\":\"channel.chat.message\"", chat);
        Assert.Contains("\"broadcaster_user_id\":\"777\"", chat);
        Assert.Contains("\"user_id\":\"777\"", chat);
        Assert.Contains("\"broadcaster_user_id\":\"777\"", other);
        Assert.DoesNotContain("\"user_id\":\"777\"", other.Replace("\"broadcaster_user_id\":\"777\"", ""));
    }

    [Fact]
    public void The_login_asks_for_the_chat_right_after_the_older_ones()
    {
        Assert.Contains("user:read:chat", TwitchClient.Scopes);
        Assert.StartsWith("moderator:read:followers channel:read:subscriptions", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasChatScope);
        Assert.False(new AppSettings { TwitchScopes = "moderator:read:followers channel:read:subscriptions" }.HasChatScope);
    }
    [Fact]
    public async Task An_already_existing_subscription_is_fine()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (409, "{\"message\":\"subscription already exists\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync("channel.subscribe", "S", CancellationToken.None);
    }

    [Fact]
    public async Task A_403_asks_for_a_new_login_a_429_and_a_400_are_plain_errors()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var status = 403;
        da.Handler = (_, _) => (status, "{\"message\":\"nope\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var auth = await Assert.ThrowsAsync<AuthRequiredException>(() => client.CreateSubscriptionAsync("t", "S", CancellationToken.None));
        Assert.Contains("channel:read:subscriptions", auth.Message);

        status = 429;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateSubscriptionAsync("t", "S", CancellationToken.None));
        status = 400;
        var bad = await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateSubscriptionAsync("t", "S", CancellationToken.None));
        Assert.Contains("nope", bad.Message);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once_and_retries()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000,\"scope\":[\"moderator:read:followers\",\"channel:read:subscriptions\"]}");
            return req.Headers["Authorization"] == "Bearer tw-access-2" ? (202, "{}") : (401, "{}");
        };
        var settings = Ready(dir);
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync("channel.subscribe", "S", CancellationToken.None);

        Assert.Equal(3, da.Requests.Count);
        Assert.Equal("tw-access-2", settings.Current.TwitchAccessToken);
    }

    [Fact]
    public async Task The_device_flow_asks_for_both_rights_and_remembers_the_granted_ones()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            switch (req.Url!.AbsolutePath)
            {
                case "/oauth2/device":
                    Assert.Contains("scopes=moderator%3Aread%3Afollowers+channel%3Aread%3Asubscriptions", body);
                    return (200, "{\"device_code\":\"dc\",\"expires_in\":600,\"interval\":0,\"user_code\":\"C\",\"verification_uri\":\"u\"}");
                case "/oauth2/token":
                    return (200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":14000,\"scope\":[\"channel:read:subscriptions\",\"moderator:read:followers\"]}");
                default:
                    return (200, "{\"data\":[{\"id\":\"4242\",\"login\":\"me\"}]}");
            }
        };
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.TwitchClientId = "twitch-client";
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var info = await client.StartDeviceFlowAsync(CancellationToken.None);
        await client.CompleteDeviceFlowAsync(info, CancellationToken.None);

        Assert.True(settings.Current.HasSubscriptionScope);
        Assert.Equal("channel:read:subscriptions moderator:read:followers", settings.Current.TwitchScopes);
    }

    [Fact]
    public void A_login_from_before_the_feature_does_not_have_the_subscription_right()
    {
        var settings = new AppSettings { TwitchScopes = "moderator:read:followers" };
        Assert.False(settings.HasSubscriptionScope);
        Assert.False(new AppSettings().HasSubscriptionScope);
        Assert.True(new AppSettings { TwitchScopes = "a channel:read:subscriptions" }.HasSubscriptionScope);
    }

    [Fact]
    public void The_websocket_url_is_derived_from_the_base_url()
    {
        Assert.Equal("ws://127.0.0.1:5/ws", TwitchEndpoints.FromBase("http://127.0.0.1:5").EventSubUrl);
        Assert.Equal("wss://example.test/ws", TwitchEndpoints.FromBase("https://example.test/").EventSubUrl);
        Assert.Equal("wss://eventsub.wss.twitch.tv/ws", TwitchEndpoints.Default.EventSubUrl);
    }
}
