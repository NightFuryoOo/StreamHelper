using System.Net;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChannelChoiceTests
{
    private static AppSettings Signed() => new() { TwitchUserId = "777", TwitchLogin = "streamer" };

    [Fact]
    public void By_default_the_app_works_on_the_own_channel()
    {
        var settings = Signed();

        Assert.True(settings.IsOwnChannel);
        Assert.Equal("777", settings.ChannelId);
        Assert.Equal("streamer", settings.ChannelLabel);
        Assert.Equal("Подключено · streamer", settings.ConnectedText);
    }

    [Fact]
    public void Choosing_a_moderated_channel_points_the_app_at_it_and_starts_its_followers_afresh()
    {
        var settings = Signed();
        settings.FollowersBaselined = true;
        settings.LastFollowerAtUtc = DateTime.UtcNow;
        settings.LastFollowerUserIds = new List<string> { "1" };

        Assert.True(settings.SelectChannel("555", "big_streamer", "Big_Streamer"));

        Assert.False(settings.IsOwnChannel);
        Assert.Equal("555", settings.ChannelId);
        Assert.Equal("Big_Streamer", settings.ChannelLabel);
        Assert.Equal("Подключено · streamer · канал Big_Streamer", settings.ConnectedText);
        Assert.False(settings.FollowersBaselined);
        Assert.Equal(default, settings.LastFollowerAtUtc);
        Assert.Empty(settings.LastFollowerUserIds);
    }

    [Fact]
    public void Choosing_the_same_channel_again_changes_nothing_and_the_own_id_means_the_own_channel()
    {
        var settings = Signed();
        settings.SelectChannel("555", "big_streamer", "");
        settings.FollowersBaselined = true;

        Assert.False(settings.SelectChannel("555", "big_streamer", "Big_Streamer"));
        Assert.True(settings.FollowersBaselined);
        Assert.Equal("Big_Streamer", settings.ChannelLabel);

        Assert.True(settings.SelectChannel("777", "streamer", "Streamer"));
        Assert.True(settings.IsOwnChannel);
        Assert.Equal("", settings.TwitchChannelId);
        Assert.False(settings.SelectChannel("", "", ""));
    }

    [Fact]
    public void Without_a_display_name_the_login_is_shown()
    {
        var settings = Signed();
        settings.SelectChannel("555", "big_streamer", "");

        Assert.Equal("big_streamer", settings.ChannelLabel);
    }

    [Fact]
    public void The_chosen_channel_stays_on_this_pc_and_is_not_put_into_a_profile()
    {
        Assert.Contains(nameof(AppSettings.TwitchChannelId), Profile.LocalSettings);
        Assert.Contains(nameof(AppSettings.TwitchChannelLogin), Profile.LocalSettings);
        Assert.Contains(nameof(AppSettings.TwitchChannelName), Profile.LocalSettings);
        Assert.DoesNotContain(nameof(AppSettings.TwitchChannelId), Profile.SharedSettings);
    }

    [Fact]
    public void Nobody_moderates_themselves_or_the_channel_owner()
    {
        var settings = Signed();
        settings.SelectChannel("555", "big_streamer", "Big_Streamer");

        Assert.False(ChatRules.CanModerate(ChatJson.Message("777", "streamer", "я"), settings));
        Assert.False(ChatRules.CanModerate(ChatJson.Message("555", "big_streamer", "стример"), settings));
        Assert.True(ChatRules.CanModerate(ChatJson.Message("42", "alice", "зритель"), settings));
        Assert.Equal("Канал · Big_Streamer", ChatRules.ChannelHeader(settings));
        Assert.Equal("Канал", ChatRules.ChannelHeader(Signed()));
        Assert.Equal("Канал · Big__Streamer", ChatRules.MenuText(ChatRules.ChannelHeader(settings)));
    }

    [Fact]
    public void The_channel_list_status_explains_what_is_missing()
    {
        var settings = Signed();
        var none = Array.Empty<ModeratedChannel>();
        var big = new[] { new ModeratedChannel("555", "big_streamer", "Big_Streamer") };

        Assert.Contains("переподключи Twitch", ChatRules.ChannelStatus(settings, null, false, null).Text);
        settings.TwitchScopes = "user:read:moderated_channels";
        Assert.Equal(("Загружаю каналы, где ты модератор…", false), ChatRules.ChannelStatus(settings, null, true, null));
        Assert.Equal(("Не удалось получить список каналов: down", true), ChatRules.ChannelStatus(settings, null, false, "down"));
        Assert.Equal(("Ты пока не модератор ни одного канала.", false), ChatRules.ChannelStatus(settings, none, false, null));
        Assert.Equal(("", false), ChatRules.ChannelStatus(settings, big, false, null));
        settings.SelectChannel("556", "gone", "Gone");
        var (text, warn) = ChatRules.ChannelStatus(settings, big, false, null);
        Assert.True(warn);
        Assert.Contains("модератором канала Gone", text);
    }
}

public class ModeratorApiTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore OnOtherChannel(TempDir dir, string scopes = TwitchClient.Scopes)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        return settings;
    }

    [Fact]
    public async Task Chat_subscriptions_name_the_chosen_channel_and_the_signed_in_moderator()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (202, "{}");
        var client = new TwitchClient(OnOtherChannel(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync(EventSubParser.ChatMessageType, "S", CancellationToken.None);
        await client.CreateSubscriptionAsync(EventSubParser.ChatNoticeType, "S", CancellationToken.None);

        Assert.All(da.Requests, r =>
        {
            Assert.Contains("\"broadcaster_user_id\":\"555\"", r);
            Assert.Contains("\"user_id\":\"777\"", r);
        });
    }

    [Fact]
    public async Task Ban_unban_and_shoutout_act_on_the_chosen_channel_as_its_moderator()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (request, _) => request.HttpMethod == "POST" && request.Url!.AbsolutePath.EndsWith("/bans") ? (200, "{\"data\":[]}") : (204, "");
        var client = new TwitchClient(OnOtherChannel(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.True((await client.BanAsync("42", 600, CancellationToken.None)).Success);
        Assert.True((await client.UnbanAsync("42", CancellationToken.None)).Success);
        Assert.True((await client.ShoutoutAsync("42", CancellationToken.None)).Success);

        Assert.StartsWith("POST /helix/moderation/bans?broadcaster_id=555&moderator_id=777 ", da.Requests[0]);
        Assert.StartsWith("DELETE /helix/moderation/bans?broadcaster_id=555&moderator_id=777&user_id=42 ", da.Requests[1]);
        Assert.StartsWith("POST /helix/chat/shoutouts?from_broadcaster_id=555&to_broadcaster_id=42&moderator_id=777 ", da.Requests[2]);
    }

    [Fact]
    public async Task Badges_stream_start_and_followers_come_from_the_chosen_channel()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[],\"pagination\":{}}");
        var client = new TwitchClient(OnOtherChannel(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.GetBadgeImagesAsync(CancellationToken.None);
        await client.GetStreamStartAsync(CancellationToken.None);
        await client.GetFollowersAsync(null, CancellationToken.None);

        Assert.Contains(da.Requests, r => r.StartsWith("GET /helix/chat/badges?broadcaster_id=555 "));
        Assert.Contains(da.Requests, r => r.StartsWith("GET /helix/streams?user_id=555 "));
        Assert.Contains(da.Requests, r => r.StartsWith("GET /helix/channels/followers?broadcaster_id=555&first=100 "));
    }

    [Fact]
    public async Task A_refusal_on_someone_elses_channel_says_you_are_not_its_moderator()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (403, "{\"error\":\"Forbidden\",\"status\":403,\"message\":\"The user in moderator_id is not one of the broadcaster's moderators.\"}");
        var client = new TwitchClient(OnOtherChannel(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal("Twitch не разрешил: ты не модератор канала Big_Streamer.", (await client.BanAsync("42", null, CancellationToken.None)).Message);
        Assert.Equal("Twitch не разрешил: ты не модератор канала Big_Streamer.", (await client.ShoutoutAsync("42", CancellationToken.None)).Message);
        Assert.Equal("Twitch не разрешил: переподключи Twitch (нужно право на модерацию).", TwitchClient.DescribeModerationFailure(HttpStatusCode.Forbidden, ""));
    }

    [Fact]
    public async Task The_moderated_channels_are_read_page_by_page_sorted_and_without_the_own_one()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (request, _) => request.Url!.Query.Contains("after=p2")
            ? (200, "{\"data\":[{\"broadcaster_id\":\"556\",\"broadcaster_login\":\"alpha\",\"broadcaster_name\":\"Alpha\"}],\"pagination\":{}}")
            : (200, "{\"data\":[{\"broadcaster_id\":\"555\",\"broadcaster_login\":\"big_streamer\",\"broadcaster_name\":\"Big_Streamer\"}," +
                    "{\"broadcaster_id\":\"777\",\"broadcaster_login\":\"streamer\",\"broadcaster_name\":\"Streamer\"}],\"pagination\":{\"cursor\":\"p2\"}}");
        var client = new TwitchClient(OnOtherChannel(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var channels = await client.GetModeratedChannelsAsync(CancellationToken.None);

        Assert.Equal(new[] { "Alpha", "Big_Streamer" }, channels.Select(c => c.Label).ToArray());
        Assert.Equal(2, da.Requests.Count);
        Assert.StartsWith("GET /helix/moderation/channels?user_id=777&first=100 ", da.Requests[0]);
        Assert.StartsWith("GET /helix/moderation/channels?user_id=777&first=100&after=p2 ", da.Requests[1]);
    }

    [Fact]
    public async Task Without_the_right_the_channel_list_is_not_asked_for()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(OnOtherChannel(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetModeratedChannelsAsync(CancellationToken.None));
        Assert.Empty(da.Requests);
        Assert.Contains("user:read:moderated_channels", TwitchClient.Scopes);
    }
}

public class ModeratorSyncTests
{
    private static SettingsStore OnOtherChannel(TempDir dir)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = TwitchClient.Scopes;
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        return settings;
    }

    [Fact]
    public async Task Subscriptions_and_rewards_pause_on_someone_elses_channel_without_opening_a_socket()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var api = new FakeEventSubApi();
        var settings = OnOtherChannel(dir);
        var subs = new SubscriptionListener(settings, api, server.Url(), _ => Task.CompletedTask) { IdleDelay = TimeSpan.FromMilliseconds(200) };
        var rewards = new RewardListener(settings, api, server.Url(), _ => Task.CompletedTask) { IdleDelay = TimeSpan.FromMilliseconds(200) };

        subs.Start();
        rewards.Start();
        try
        {
            Assert.True(await Wait.Until(() => subs.Status.Message.Contains("только на своём канале") && rewards.Status.Message.Contains("только на своём канале")));
            Assert.Equal("Подписки: только на своём канале. Сейчас выбран канал Big_Streamer (вкладка «Twitch»).", subs.Status.Message);
            Assert.Equal(0, server.ConnectionCount);
            Assert.Equal(0, api.CallCount);
        }
        finally
        {
            subs.Stop();
            rewards.Stop();
        }
    }

    [Fact]
    public async Task The_chat_connection_follows_the_chosen_channel_and_says_which_one()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-O"));
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi();
        var listener = new PingListener(OnOtherChannel(dir), api, server.Url(), _ => Task.CompletedTask) { IdleDelay = TimeSpan.FromMilliseconds(200) };

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.Ok));
            Assert.Equal("Подключено · streamer · канал Big_Streamer", listener.Status.Message);
            Assert.Equal(2, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Polls_and_predictions_are_not_read_on_someone_elses_channel()
    {
        using var dir = new TempDir();
        var api = new FakeVoteApi();
        var watcher = new VoteWatcher(OnOtherChannel(dir), api);

        Assert.False(await watcher.PollOnceAsync(CancellationToken.None));

        Assert.Equal(0, api.PollReads + api.PredictionReads);
        Assert.Null(watcher.Poll);
        Assert.Equal("Опросы и предикты только на своём канале.", watcher.Status.Message);
    }

    [Fact]
    public void Clearing_the_feed_forgets_the_old_channel_chat()
    {
        var feed = new ChatFeed();
        var message = ChatJson.Message("42", "alice", "привет");
        feed.Push(message);

        feed.Clear();

        Assert.Empty(feed.Recent());
        Assert.Empty(feed.HistoryOf("42"));
        var seen = new List<ChatMessage>();
        feed.Message += seen.Add;
        feed.Push(message);
        Assert.Single(seen);
    }

    private sealed class CountingBadges : IBadgeApi
    {
        public int Calls;
        public TaskCompletionSource Gate = new();

        public async Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct)
        {
            Calls++;
            var call = Calls;
            if (call == 1) await Gate.Task;
            return new Dictionary<string, string> { ["subscriber/0"] = $"https://cdn/sub{call}.png" };
        }
    }

    [Fact]
    public async Task A_reset_badge_catalog_loads_again_and_ignores_the_old_channel_answer()
    {
        var api = new CountingBadges();
        var catalog = new ChatBadgeCatalog(api);
        var stale = catalog.EnsureLoadedAsync();

        catalog.Reset();
        await catalog.EnsureLoadedAsync();
        api.Gate.SetResult();
        await stale;

        Assert.Equal(2, api.Calls);
        Assert.Equal("https://cdn/sub2.png", catalog.UrlFor("subscriber", "0"));
    }

    private sealed class ChannelSevenTv : ISevenTvApi
    {
        public List<string> Asked = new();

        public Task<IReadOnlyList<SevenTvEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SevenTvEmote>>(Array.Empty<SevenTvEmote>());

        public Task<IReadOnlyList<SevenTvEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
        {
            Asked.Add(twitchUserId);
            return Task.FromResult<IReadOnlyList<SevenTvEmote>>(new[] { new SevenTvEmote("Pog" + twitchUserId, "https://cdn/" + twitchUserId, false) });
        }
    }

    [Fact]
    public async Task A_reset_7tv_catalog_loads_the_new_channel_emotes()
    {
        var api = new ChannelSevenTv();
        var channel = "777";
        var catalog = new SevenTvCatalog(api, () => channel);
        await catalog.EnsureLoadedAsync();

        channel = "555";
        catalog.Reset();
        Assert.Equal(0, catalog.Count);
        await catalog.EnsureLoadedAsync();

        Assert.Equal(new[] { "777", "555" }, api.Asked.ToArray());
        Assert.True(catalog.TryGet("Pog555", out _));
        Assert.False(catalog.TryGet("Pog777", out _));
    }
}
