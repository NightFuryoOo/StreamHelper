using System.Text.Json;
using System.Text.Json.Serialization;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class BitsParsingTests
{
    private static Subscriber? Parse(string ev) =>
        EventSubParser.Parse(MockEventSubServer.Notification("b1", EventSubParser.BitsUseType, ev))?.Subscriber;

    [Fact]
    public void A_cheer_becomes_a_bits_entry_and_the_cheermotes_are_left_out_of_the_message()
    {
        var bits = Parse("{\"user_id\":\"9\",\"user_login\":\"alice\",\"user_name\":\"Alice\",\"bits\":150,\"type\":\"cheer\",\"power_up\":null," +
                         "\"message\":{\"text\":\"Cheer100 great  stream Cheer50\",\"fragments\":[" +
                         "{\"type\":\"cheermote\",\"text\":\"Cheer100\",\"cheermote\":{\"prefix\":\"cheer\",\"bits\":100,\"tier\":100}}," +
                         "{\"type\":\"text\",\"text\":\" great  stream \"}," +
                         "{\"type\":\"cheermote\",\"text\":\"Cheer50\",\"cheermote\":{\"prefix\":\"cheer\",\"bits\":50,\"tier\":1}}]}}")!;

        Assert.Equal(SubscriptionKind.Bits, bits.Kind);
        Assert.True(bits.IsBits);
        Assert.Equal(150, bits.Bits);
        Assert.Equal("Alice", bits.Name);
        Assert.Equal("great stream", bits.Message);
        Assert.Equal("Bits", bits.KindText);
        Assert.Equal("150 Bits", bits.DetailText);
        Assert.Equal("Bits", bits.ToastTitle);
        Assert.Equal("", bits.Tier);
    }

    [Fact]
    public void A_power_up_names_what_was_bought()
    {
        var giant = Parse("{\"user_login\":\"bob\",\"user_name\":\"Bob\",\"bits\":100,\"type\":\"power_up\"," +
                          "\"power_up\":{\"type\":\"gigantify_an_emote\",\"emote\":{\"id\":\"25\",\"name\":\"Kappa\"},\"message_effect_id\":null}," +
                          "\"message\":{\"text\":\"Kappa\",\"fragments\":[{\"type\":\"emote\",\"text\":\"Kappa\"}]}}")!;
        var effect = Parse("{\"user_login\":\"bob\",\"user_name\":\"Bob\",\"bits\":30,\"type\":\"power_up\",\"power_up\":{\"type\":\"message_effect\",\"message_effect_id\":\"cosmic\"}}")!;
        var celebration = Parse("{\"user_login\":\"bob\",\"user_name\":\"Bob\",\"bits\":200,\"type\":\"power_up\",\"power_up\":{\"type\":\"celebration\"}}")!;
        var custom = Parse("{\"user_login\":\"bob\",\"user_name\":\"Bob\",\"bits\":500,\"type\":\"custom_power_up\",\"custom_power_up\":{\"title\":\"Спой песню\",\"reward_id\":\"r1\"}}")!;

        Assert.Equal("большой смайл Kappa", giant.PowerUp);
        Assert.Equal("100 Bits · большой смайл Kappa", giant.DetailText);
        Assert.Equal("Power-up", giant.KindText);
        Assert.Equal("Power-up за Bits", giant.ToastTitle);
        Assert.Equal("Kappa", giant.Message);
        Assert.Equal("эффект сообщения", effect.PowerUp);
        Assert.Equal("", effect.Message);
        Assert.Equal("празднование", celebration.PowerUp);
        Assert.Equal("«Спой песню»", custom.PowerUp);
    }

    [Fact]
    public void Missing_names_mean_an_anonymous_viewer_and_zero_bits_are_dropped()
    {
        var anonymous = Parse("{\"user_id\":null,\"user_login\":null,\"user_name\":null,\"bits\":1000,\"type\":\"cheer\",\"message\":{\"text\":\"hi\",\"fragments\":[]}}")!;

        Assert.True(anonymous.IsAnonymous);
        Assert.Equal("Аноним", anonymous.Name);
        Assert.Equal("1 000 Bits", System.Text.RegularExpressions.Regex.Replace(anonymous.DetailText, @"\s", " "));
        Assert.Equal("hi", anonymous.Message);
        Assert.Null(Parse("{\"user_login\":\"x\",\"user_name\":\"X\",\"bits\":0,\"type\":\"cheer\"}"));
    }

    [Fact]
    public void Bits_toasts_follow_their_own_switch()
    {
        var settings = new AppSettings { NotifySubscribers = true, NotifyBits = false };
        var bits = new Subscriber { Kind = SubscriptionKind.Bits, Bits = 5, DisplayName = "A" };
        var sub = new Subscriber { Kind = SubscriptionKind.New, DisplayName = "B" };

        Assert.False(EventToasts.Notifies(settings, bits));
        Assert.True(EventToasts.Notifies(settings, sub));
        settings.NotifyBits = true;
        settings.NotifySubscribers = false;
        Assert.True(EventToasts.Notifies(settings, bits));
        Assert.False(EventToasts.Notifies(settings, sub));

        Assert.Equal("Bits: 2", EventToasts.Subscribers(new[] { bits, bits }).Title);
        Assert.Equal("Подписок и Bits: 2", EventToasts.Subscribers(new[] { sub, bits }).Title);
        Assert.Equal("Новых подписок: 2", EventToasts.Subscribers(new[] { sub, sub }).Title);
        Assert.Equal("A · 5 Bits", EventToasts.Subscribers(new[] { bits }).Line);
    }

    [Fact]
    public void Bits_are_part_of_the_requested_rights()
    {
        Assert.EndsWith(" bits:read", TwitchClient.Scopes);
        Assert.False(new AppSettings { TwitchScopes = "channel:read:subscriptions" }.HasBitsScope);
        Assert.True(new AppSettings { TwitchScopes = "channel:read:subscriptions bits:read" }.HasBitsScope);
    }
}

public class BitsStorageTests
{
    private enum OldKind
    {
        New,
        Resub,
        Gift,
    }

    private sealed class OldSubscriber
    {
        public string Key { get; set; } = "";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public OldKind Kind { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    [Fact]
    public void Bits_are_written_as_a_number_so_the_previous_version_still_reads_the_file_and_keeps_the_fields()
    {
        using var dir = new TempDir();
        var store = new SubscriberStore(dir.File("subscribers.json"));
        store.AddRange(new[]
        {
            new Subscriber { Key = "s", Kind = SubscriptionKind.Resub, DisplayName = "S", Months = 3, AtUtc = MakeFollower.T0 },
            new Subscriber { Key = "b", Kind = SubscriptionKind.Bits, DisplayName = "B", Bits = 300, PowerUp = "празднование", AtUtc = MakeFollower.T0.AddMinutes(1) },
        });
        var json = File.ReadAllText(dir.File("subscribers.json"));

        Assert.Contains("\"Kind\": \"Resub\"", json);
        Assert.Contains("\"Kind\": 3", json);
        var old = JsonSerializer.Deserialize<List<OldSubscriber>>(json)!;
        Assert.Equal(2, old.Count);
        Assert.Equal(300, old.Single(o => o.Key == "b").Extra!["Bits"].GetInt32());

        var reloaded = new SubscriberStore(dir.File("subscribers.json"));
        var bits = reloaded.Items.Single(i => i.Key == "b");
        Assert.Equal(SubscriptionKind.Bits, bits.Kind);
        Assert.Equal(300, bits.Bits);
        Assert.Equal("празднование", bits.PowerUp);
        Assert.Equal(SubscriptionKind.Resub, reloaded.Items.Single(i => i.Key == "s").Kind);
    }

    [Fact]
    public void Old_files_and_unknown_kinds_still_load()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("subscribers.json"),
            "[{\"Key\":\"a\",\"Kind\":\"Gift\",\"GiftTotal\":5},{\"Key\":\"b\",\"Kind\":\"bits\",\"Bits\":7},{\"Key\":\"c\",\"Kind\":\"Hype\"},{\"Key\":\"d\",\"Kind\":2}]");

        var items = new SubscriberStore(dir.File("subscribers.json")).Items;

        Assert.Equal(new[] { SubscriptionKind.Gift, SubscriptionKind.Bits, SubscriptionKind.New, SubscriptionKind.Gift }, items.Select(i => i.Kind).ToArray());
    }
}

public class BitsListenerTests
{
    private static SubscriptionListener NewListener(SettingsStore settings, IEventSubApi api, string url, List<Subscriber> delivered) =>
        new(settings, api, url, items =>
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

    private static async Task<(string[] Types, List<Subscriber> Delivered, SyncStatus Status)> RunAsync(string scopes, BitsApi api)
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-B"));
            await Task.Delay(300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("c1", EventSubParser.BitsUseType,
                "{\"user_login\":\"alice\",\"user_name\":\"Alice\",\"bits\":42,\"type\":\"cheer\",\"message\":{\"text\":\"Cheer42 hi\",\"fragments\":[{\"type\":\"cheermote\",\"text\":\"Cheer42\"},{\"type\":\"text\",\"text\":\" hi\"}]}}"));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", "channel.subscribe", MockEventSubServer.NewSubEvent("bob", "Bob")));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        var delivered = new List<Subscriber>();
        var listener = NewListener(settings, api, server.Url(), delivered);
        listener.Start();
        try
        {
            await Wait.Until(() => { lock (delivered) return delivered.Count >= 2; });
            await Task.Delay(200);
            lock (api.Calls) return (api.Calls.Select(c => c.Type).ToArray(), delivered, listener.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task With_the_bits_right_the_bits_events_are_subscribed_and_delivered()
    {
        var (types, delivered, status) = await RunAsync("channel:read:subscriptions bits:read", new BitsApi());

        Assert.Equal(SubscriptionListener.SubscriptionTypes.Append(EventSubParser.BitsUseType).ToArray(), types);
        Assert.Equal(SyncState.Ok, status.State);
        lock (delivered)
        {
            var bits = delivered.Single(d => d.IsBits);
            Assert.Equal((42, "hi", "Alice"), (bits.Bits, bits.Message, bits.Name));
            Assert.Contains(delivered, d => d.Kind == SubscriptionKind.New);
        }
    }

    [Fact]
    public async Task Without_the_bits_right_only_the_subscriptions_are_asked_for()
    {
        var (types, _, status) = await RunAsync("channel:read:subscriptions", new BitsApi());

        Assert.Equal(SubscriptionListener.SubscriptionTypes, types);
        Assert.Equal(SyncState.Ok, status.State);
    }

    [Fact]
    public async Task A_refused_bits_subscription_does_not_stop_the_subscriptions()
    {
        var api = new BitsApi { RefusedType = EventSubParser.BitsUseType };

        var (types, delivered, status) = await RunAsync("channel:read:subscriptions bits:read", api);

        Assert.Contains(EventSubParser.BitsUseType, types);
        Assert.Equal(SyncState.Ok, status.State);
        lock (delivered) Assert.Contains(delivered, d => d.Kind == SubscriptionKind.New);
    }

    private sealed class BitsApi : IEventSubApi
    {
        public List<(string Type, string SessionId)> Calls { get; } = new();

        public string RefusedType { get; init; } = "";

        public Task CreateSubscriptionAsync(string type, string sessionId, CancellationToken ct)
        {
            lock (Calls) Calls.Add((type, sessionId));
            return type == RefusedType ? Task.FromException(new AuthRequiredException("Twitch не разрешил")) : Task.CompletedTask;
        }
    }
}
