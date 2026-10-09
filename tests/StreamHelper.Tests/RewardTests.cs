using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class RewardParserTests
{
    private static string RedemptionEvent(string id = "r-1", string user = "Alice", string input = "Сыграй на гитаре", string title = "Заказать трек", long cost = 500, string redeemedAt = "2026-10-05T12:30:00.1234567Z") =>
        "{\"id\":\"" + id + "\",\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\"," +
        "\"user_id\":\"9\",\"user_login\":\"" + user.ToLowerInvariant() + "\",\"user_name\":\"" + user + "\",\"user_input\":\"" + input + "\"," +
        "\"status\":\"unfulfilled\",\"reward\":{\"id\":\"rw1\",\"title\":\"" + title + "\",\"cost\":" + cost + ",\"prompt\":\"Напиши название\"}," +
        "\"redeemed_at\":\"" + redeemedAt + "\"}";

    [Fact]
    public void Parses_a_redemption_with_reward_cost_input_and_the_redeem_time()
    {
        var json = MockEventSubServer.Notification("msg-1", EventSubParser.RedemptionType, RedemptionEvent());

        var message = EventSubParser.Parse(json)!;

        Assert.Null(message.Subscriber);
        var redemption = message.Redemption!;
        Assert.Equal("r-1", redemption.Key);
        Assert.Equal("Alice", redemption.Name);
        Assert.Equal("Заказать трек", redemption.Title);
        Assert.Equal(500, redemption.Cost);
        Assert.Equal("Сыграй на гитаре", redemption.UserInput);
        Assert.True(redemption.HasInput);
        Assert.Equal(new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc).AddTicks(1234567), redemption.AtUtc);
    }

    [Fact]
    public void A_redemption_without_input_or_a_reward_is_still_usable()
    {
        var ev = "{\"id\":\"r-2\",\"user_login\":\"bob\",\"user_name\":\"\",\"user_input\":\"\",\"status\":\"fulfilled\"}";

        var redemption = EventSubParser.Parse(MockEventSubServer.Notification("msg-2", EventSubParser.RedemptionType, ev))!.Redemption!;

        Assert.Equal("bob", redemption.Name);
        Assert.Equal("Награда", redemption.Title);
        Assert.Equal(0, redemption.Cost);
        Assert.False(redemption.HasInput);
    }

    [Fact]
    public void A_missing_redemption_id_falls_back_to_the_message_id_and_a_null_cost_is_tolerated()
    {
        var ev = "{\"user_login\":\"x\",\"user_name\":\"X\",\"user_input\":null,\"reward\":{\"title\":\"T\",\"cost\":null}}";

        var redemption = EventSubParser.Parse(MockEventSubServer.Notification("msg-3", EventSubParser.RedemptionType, ev))!.Redemption!;

        Assert.Equal("msg-3", redemption.Key);
        Assert.Equal(0, redemption.Cost);
        Assert.Equal("", redemption.UserInput);
    }

    [Theory]
    [InlineData(1, "1 балл")]
    [InlineData(2, "2 балла")]
    [InlineData(4, "4 балла")]
    [InlineData(5, "5 баллов")]
    [InlineData(11, "11 баллов")]
    [InlineData(12, "12 баллов")]
    [InlineData(21, "21 балл")]
    [InlineData(22, "22 балла")]
    [InlineData(100, "100 баллов")]
    [InlineData(111, "111 баллов")]
    [InlineData(1000, "1 000 баллов")]
    [InlineData(250000, "250 000 баллов")]
    public void Formats_the_cost_with_the_right_russian_plural(long cost, string expected) =>
        Assert.Equal(expected, Redemption.FormatCost(cost));

    [Fact]
    public void Marking_a_redemption_done_also_marks_it_seen()
    {
        var redemption = new Redemption { Key = "a" };

        redemption.Done = true;

        Assert.True(redemption.Seen);
        Assert.False(redemption.IsNew);
    }

    [Fact]
    public void A_subscription_notification_is_not_mistaken_for_a_redemption()
    {
        var json = MockEventSubServer.Notification("m", "channel.subscribe", MockEventSubServer.NewSubEvent("a", "A"));

        var message = EventSubParser.Parse(json)!;

        Assert.Null(message.Redemption);
        Assert.NotNull(message.Subscriber);
    }

    [Fact]
    public void The_store_dedupes_by_redemption_id_orders_newest_first_and_persists_seen_and_done()
    {
        using var dir = new TempDir();
        var path = dir.File("redemptions.json");
        var store = new RedemptionStore(path);
        var older = new Redemption { Key = "a", DisplayName = "A", RewardTitle = "T", Cost = 100, AtUtc = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc) };
        var newer = new Redemption { Key = "b", DisplayName = "B", RewardTitle = "T", Cost = 100, AtUtc = new DateTime(2026, 10, 5, 12, 1, 0, DateTimeKind.Utc) };

        store.AddRange(new[] { newer, older });
        var again = store.AddRange(new[] { new Redemption { Key = "a" } });

        Assert.Empty(again);
        Assert.Equal(new[] { "b", "a" }, store.Items.Select(r => r.Key).ToArray());
        Assert.Equal(2, store.UnseenCount);

        store.Items[0].Done = true;
        Assert.Equal(1, store.UnseenCount);

        var reloaded = new RedemptionStore(path);
        Assert.True(reloaded.Items[0].Done);
        Assert.True(reloaded.Items[0].Seen);
        Assert.False(reloaded.Items[1].Seen);
        Assert.Equal(1, reloaded.UnseenCount);
    }
}

public class RewardFilterTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "moderator:read:followers channel:read:subscriptions channel:read:redemptions")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private const string RewardsJson =
        "{\"data\":[" +
        "{\"id\":\"b\",\"title\":\"Заказать трек\",\"cost\":500,\"is_enabled\":true,\"prompt\":\"x\"}," +
        "{\"id\":\"a\",\"title\":\"Вода\",\"cost\":1,\"is_enabled\":false}," +
        "{\"id\":\"c\",\"title\":\"Ещё\",\"cost\":2500}," +
        "{\"title\":\"no id\",\"cost\":5}]}";

    [Fact]
    public void Every_reward_is_allowed_by_default()
    {
        var settings = new AppSettings();
        Assert.True(settings.AllowsReward("anything"));
        Assert.True(settings.AllowsReward(""));
    }

    [Fact]
    public void Settings_saved_with_the_old_pick_list_filter_still_load_and_every_reward_is_shown()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"),
            "{\"ShowAllRewards\":false,\"RewardSelectionInitialized\":true,\"SelectedRewardIds\":[\"a\"],\"NotifyRewards\":false}");

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.False(loaded.NotifyRewards);
        Assert.True(loaded.AllowsReward("a"));
        Assert.True(loaded.AllowsReward("b"));
    }

    [Fact]
    public void Only_own_rewards_narrows_the_filter_to_the_created_ones()
    {
        var settings = new AppSettings
        {
            ManagedRewardIds = new List<string> { "copy1", "copy2" },
            OnlyOwnRewards = true,
        };

        Assert.True(settings.AllowsReward("copy1"));
        Assert.True(settings.AllowsReward("copy2"));
        Assert.False(settings.AllowsReward("original"));
        Assert.False(settings.AllowsReward(""));
    }

    [Fact]
    public void Only_own_rewards_with_nothing_created_shows_nothing_and_off_is_the_default()
    {
        Assert.False(new AppSettings().OnlyOwnRewards);

        var settings = new AppSettings { OnlyOwnRewards = true };
        Assert.False(settings.AllowsReward("anything"));
    }
    [Fact]
    public void The_only_own_rewards_choice_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.OnlyOwnRewards = true;
        store.Current.ManagedRewardIds = new List<string> { "copy1" };
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.True(reloaded.OnlyOwnRewards);
        Assert.True(reloaded.AllowsReward("copy1"));
        Assert.False(reloaded.AllowsReward("other"));
    }

    [Fact]
    public void A_redemption_carries_the_id_of_its_reward()
    {
        var ev = "{\"id\":\"r-1\",\"user_login\":\"a\",\"user_name\":\"A\",\"reward\":{\"id\":\"reward-42\",\"title\":\"T\",\"cost\":10}}";

        var redemption = EventSubParser.Parse(MockEventSubServer.Notification("m", EventSubParser.RedemptionType, ev))!.Redemption!;

        Assert.Equal("reward-42", redemption.RewardId);
    }

    [Fact]
    public void Parses_the_reward_list_sorted_by_title_skipping_entries_without_an_id()
    {
        var rewards = TwitchClient.ParseRewards(RewardsJson);

        Assert.Equal(new[] { "a", "c", "b" }, rewards.Select(r => r.Id).ToArray());
        Assert.False(rewards[0].IsEnabled);
        Assert.True(rewards[1].IsEnabled);
        Assert.Equal(500, rewards[2].Cost);
    }

    [Fact]
    public async Task Requests_the_broadcasters_rewards_with_the_bearer_token()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            Assert.Equal("GET", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.Equal("twitch-client", req.Headers["Client-Id"]);
            return (200, RewardsJson);
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var rewards = await client.GetRewardsAsync(CancellationToken.None);

        Assert.Equal(3, rewards.Count);
        Assert.Contains("GET /helix/channel_points/custom_rewards?broadcaster_id=777", Assert.Single(da.Requests));
    }

    [Fact]
    public async Task Without_the_redemption_right_no_request_is_made_and_a_new_login_is_requested()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "moderator:read:followers"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetRewardsAsync(CancellationToken.None));

        Assert.Empty(da.Requests);
    }

    [Fact]
    public async Task A_401_refreshes_once_a_403_asks_for_a_new_login_and_a_500_is_a_plain_error()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var status = 401;
        da.Handler = (req, _) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000}");
            if (status == 401) return req.Headers["Authorization"] == "Bearer tw-access-2" ? (200, RewardsJson) : (401, "{}");
            return (status, "{}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal(3, (await client.GetRewardsAsync(CancellationToken.None)).Count);

        status = 403;
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetRewardsAsync(CancellationToken.None));
        status = 500;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRewardsAsync(CancellationToken.None));
    }

    [Fact]
    public void The_rewards_url_is_derived_from_the_base_url()
    {
        Assert.Equal("http://127.0.0.1:5/helix/channel_points/custom_rewards", TwitchEndpoints.FromBase("http://127.0.0.1:5").RewardsUrl);
        Assert.Equal("https://api.twitch.tv/helix/channel_points/custom_rewards", TwitchEndpoints.Default.RewardsUrl);
    }
}

public class RewardListenerTests
{
    private static SettingsStore Settings(TempDir dir, string scopes)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private static RewardListener NewListener(SettingsStore settings, FakeEventSubApi api, string url, List<Redemption> delivered) =>
        new(settings, api, url, items =>
        {
            lock (delivered) delivered.AddRange(items);
            return Task.CompletedTask;
        })
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
        };

    private static int Count(List<Redemption> list)
    {
        lock (list) return list.Count;
    }

    [Fact]
    public async Task Subscribes_only_to_the_redemption_type_and_delivers_each_redemption_once()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var ev = "{\"id\":\"r-1\",\"user_login\":\"alice\",\"user_name\":\"Alice\",\"user_input\":\"hi\",\"reward\":{\"title\":\"Трек\",\"cost\":300},\"redeemed_at\":\"2026-10-05T12:30:00Z\"}";
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-R"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", EventSubParser.RedemptionType, ev));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", EventSubParser.RedemptionType, ev));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", "channel.subscribe", MockEventSubServer.NewSubEvent("x", "X")));
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi();
        var delivered = new List<Redemption>();
        var listener = NewListener(Settings(dir, "channel:read:redemptions"), api, server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) >= 1));
            await Task.Delay(300);

            var call = Assert.Single(api.Calls);
            Assert.Equal(EventSubParser.RedemptionType, call.Type);
            Assert.Equal("SESS-R", call.SessionId);
            Assert.Equal(1, Count(delivered));
            Assert.Equal(SyncState.Ok, listener.Status.State);
            Assert.StartsWith("Подключено · ", listener.Status.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Without_the_redemption_right_it_asks_to_reconnect_and_does_not_open_a_socket()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var api = new FakeEventSubApi();
        var listener = NewListener(Settings(dir, "moderator:read:followers channel:read:subscriptions"), api, server.Url(), new List<Redemption>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NeedsLogin));
            Assert.Contains("наград", listener.Status.Message);
            Assert.Equal(0, server.ConnectionCount);
            Assert.Equal(0, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Subscriptions_keep_working_when_only_the_subscription_right_was_granted()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome());
            await Task.Delay(Timeout.Infinite);
        };
        var settings = Settings(dir, "moderator:read:followers channel:read:subscriptions");
        var subsApi = new FakeEventSubApi();
        var rewardsApi = new FakeEventSubApi();
        var subs = new SubscriptionListener(settings, subsApi, server.Url(), _ => Task.CompletedTask)
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400), IdleDelay = TimeSpan.FromMilliseconds(200),
        };
        var rewards = NewListener(settings, rewardsApi, server.Url(), new List<Redemption>());

        subs.Start();
        rewards.Start();
        try
        {
            Assert.True(await Wait.Until(() => subs.Status.State == SyncState.Ok));
            Assert.True(await Wait.Until(() => rewards.Status.State == SyncState.NeedsLogin));
            Assert.Equal(3, subsApi.CallCount);
            Assert.Equal(0, rewardsApi.CallCount);
        }
        finally
        {
            subs.Stop();
            rewards.Stop();
        }
    }

    [Fact]
    public void The_device_flow_asks_for_the_redemption_right_too()
    {
        Assert.Contains("channel:read:redemptions", TwitchClient.Scopes);
        Assert.Contains("channel:read:subscriptions", TwitchClient.Scopes);
        Assert.Contains("moderator:read:followers", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = "channel:read:redemptions" }.HasRedemptionScope);
        Assert.False(new AppSettings { TwitchScopes = "channel:read:subscriptions" }.HasRedemptionScope);
    }
}
