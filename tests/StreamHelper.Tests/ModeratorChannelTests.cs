using System.Net;
using StreamHelper.Api;
using StreamHelper.Models;
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
            Assert.Equal(5, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task On_someone_elses_channel_the_chat_flows_but_pings_raids_and_streaks_stay_silent()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-Q"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType,
                ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", ("777", "streamer"))));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", EventSubParser.ChatNoticeType, NoticeJson.Raid("nm-1", "raider", "Raider", 50)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", EventSubParser.ChatMessageType, ChatJson.Event("m2", "43", "bob", "Bob", "последнее")));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = OnOtherChannel(dir);
        var pings = new List<ChatPing>();
        var moments = new List<ChannelMoment>();
        var chat = new List<ChatMessage>();
        var listener = new PingListener(settings, new FakeEventSubApi(), server.Url(), items =>
        {
            lock (pings) pings.AddRange(items);
            return Task.CompletedTask;
        }, message =>
        {
            lock (chat) chat.Add(message);
        }, moment =>
        {
            lock (moments) moments.Add(moment);
        }) { IdleDelay = TimeSpan.FromMilliseconds(200) };

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => { lock (chat) return chat.Count >= 2; }));
            await Task.Delay(300);
            lock (chat) Assert.Equal(new[] { ("m1", true), ("m2", false) }, chat.Select(m => (m.MessageId, m.IsPing)).ToArray());
            lock (pings) Assert.Empty(pings);
            lock (moments) Assert.Empty(moments);
            Assert.Equal("Подключено · streamer · канал Big_Streamer", listener.Status.Message);
            var tab = PingListener.TabStatus(listener.Status, settings.Current);
            Assert.Equal(SyncState.NotConnected, tab.State);
            Assert.Equal("Пинги: только на своём канале. Сейчас выбран канал Big_Streamer (вкладка «Twitch»).", tab.Message);
            Assert.Equal("Рейды и стрики: только на своём канале. Сейчас выбран канал Big_Streamer (вкладка «Twitch»).", MainViewModel.MomentStatusText(tab));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void On_the_own_channel_the_ping_tabs_show_the_connection_as_it_is()
    {
        var own = new AppSettings { TwitchUserId = "777", TwitchLogin = "streamer" };
        var status = new SyncStatus(SyncState.Ok, "Подключено · streamer");

        Assert.Same(status, PingListener.TabStatus(status, own));
        var other = new AppSettings { TwitchUserId = "777", TwitchLogin = "streamer" };
        other.SelectChannel("555", "big_streamer", "Big_Streamer");
        var error = new SyncStatus(SyncState.Error, "Пинги: нет связи");
        Assert.Same(error, PingListener.TabStatus(error, other));
    }

    [Fact]
    public async Task Followers_are_not_polled_on_someone_elses_channel()
    {
        using var dir = new TempDir();
        var settings = OnOtherChannel(dir);
        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("a", 10)) });
        var delivered = new List<Follower>();
        var poller = new FollowerPoller(settings, source, items =>
        {
            delivered.AddRange(items);
            return Task.CompletedTask;
        });

        Assert.False(await poller.PollOnceAsync(CancellationToken.None));

        Assert.Empty(source.Requested);
        Assert.Empty(delivered);
        Assert.Equal(SyncState.NotConnected, poller.Status.State);
        Assert.Equal("Фолловеры: только на своём канале. Сейчас выбран канал Big_Streamer (вкладка «Twitch»).", poller.Status.Message);
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

    private sealed class ChannelSevenTv : IExtraEmoteApi
    {
        public List<string> Asked = new();

        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());

        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
        {
            Asked.Add(twitchUserId);
            return Task.FromResult<IReadOnlyList<ExtraEmote>>(new[] { new ExtraEmote("Pog" + twitchUserId, "https://cdn/" + twitchUserId, false) });
        }
    }

    [Fact]
    public async Task A_reset_7tv_catalog_loads_the_new_channel_emotes()
    {
        var api = new ChannelSevenTv();
        var channel = "777";
        var catalog = new ExtraEmoteCatalog(api, () => channel);
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

public class ViewerChannelTests
{
    private static AppSettings Signed() => new() { TwitchUserId = "777", TwitchLogin = "streamer" };

    [Theory]
    [InlineData("shroud", "shroud")]
    [InlineData("  @Shroud ", "shroud")]
    [InlineData("https://www.twitch.tv/Shroud/videos", "shroud")]
    [InlineData("twitch.tv/some_user_42?referrer=raid", "some_user_42")]
    [InlineData("", null)]
    [InlineData("two words", null)]
    [InlineData("ab", null)]
    [InlineData("канал", null)]
    [InlineData("https://www.twitch.tv/", null)]
    public void A_channel_is_typed_as_a_nick_or_a_link(string text, string? expected)
    {
        Assert.Equal(expected, ChatRules.ChannelLogin(text));
    }

    [Fact]
    public void A_channel_joined_as_a_viewer_is_told_apart_from_a_moderated_one()
    {
        var settings = Signed();

        Assert.True(settings.SelectChannel("900", "viewme", "ViewMe", asViewer: true));
        Assert.True(settings.IsViewerChannel);
        Assert.Equal("900", settings.ChannelId);
        Assert.Equal("Подключено · streamer · канал ViewMe (зритель)", settings.ConnectedText);
        Assert.Equal("Канал · ViewMe (зритель)", ChatRules.ChannelHeader(settings));
        Assert.False(settings.SelectChannel("900", "viewme", "ViewMe", asViewer: true));

        Assert.True(settings.SelectChannel("900", "viewme", "ViewMe"));
        Assert.False(settings.IsViewerChannel);
        Assert.Equal("Подключено · streamer · канал ViewMe", settings.ConnectedText);

        Assert.True(settings.SelectChannel("777", "streamer", "Streamer", asViewer: true));
        Assert.True(settings.IsOwnChannel);
        Assert.False(settings.IsViewerChannel);
    }

    [Fact]
    public void On_a_viewer_channel_nobody_can_be_banned_muted_or_shouted_out()
    {
        var settings = Signed();
        var message = new ChatMessage("m1", "42", "alice", "Alice", "привет", Array.Empty<ChatMention>(), DateTime.UtcNow);

        settings.SelectChannel("900", "viewme", "ViewMe");
        Assert.True(ChatRules.CanModerate(message, settings));
        settings.SelectChannel("900", "viewme", "ViewMe", asViewer: true);
        Assert.False(ChatRules.CanModerate(message, settings));
    }

    [Fact]
    public void A_viewer_channel_missing_from_the_moderated_list_is_not_a_warning()
    {
        var settings = Signed();
        settings.TwitchScopes = TwitchClient.Scopes;
        var moderated = new[] { new ModeratedChannel("555", "big_streamer", "Big_Streamer") };

        settings.SelectChannel("900", "viewme", "ViewMe", asViewer: true);
        Assert.Equal(("", false), ChatRules.ChannelStatus(settings, moderated, false, null));
        settings.SelectChannel("900", "viewme", "ViewMe");
        Assert.True(ChatRules.ChannelStatus(settings, moderated, false, null).Warn);
    }

    [Fact]
    public void Viewer_channels_are_kept_newest_first_without_repeats_and_up_to_ten()
    {
        var settings = Signed();
        for (var i = 1; i <= 12; i++) settings.RememberViewerChannel(new SavedChannel("id" + i, "c" + i, "C" + i));
        settings.RememberViewerChannel(new SavedChannel("id5", "c5", "C5 renamed"));

        Assert.Equal(AppSettings.ViewerChannelLimit, settings.ViewerChannels.Count);
        Assert.Equal("C5 renamed", settings.ViewerChannels[0].Label);
        Assert.Equal(new[] { "id5", "id12", "id11" }, settings.ViewerChannels.Take(3).Select(c => c.Id).ToArray());
        Assert.True(settings.ForgetViewerChannel("id12"));
        Assert.False(settings.ForgetViewerChannel("id12"));
        Assert.Equal("c", new SavedChannel("x", "c", "").Label);
    }

    [Fact]
    public void Viewer_channels_and_the_viewer_choice_are_saved_on_this_pc()
    {
        using var dir = new TempDir();
        var settings = MakeFollower.Settings(dir);
        settings.Current.RememberViewerChannel(new SavedChannel("900", "viewme", "ViewMe"));
        settings.Current.SelectChannel("900", "viewme", "ViewMe", asViewer: true);
        settings.Save();

        var json = File.ReadAllText(dir.File("settings.json"));
        var again = new SettingsStore(dir.File("settings.json"));

        Assert.DoesNotContain("\"Label\"", json);
        Assert.Equal(new[] { new SavedChannel("900", "viewme", "ViewMe") }, again.Current.ViewerChannels);
        Assert.True(again.Current.IsViewerChannel);
        Assert.Contains(nameof(AppSettings.ViewerChannels), Profile.LocalSettings);
        Assert.Contains(nameof(AppSettings.TwitchChannelAsViewer), Profile.LocalSettings);
    }

    [Fact]
    public async Task A_channel_is_found_by_its_nick()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (request, _) => request.QueryString["login"] == "viewme"
            ? (200, "{\"data\":[{\"id\":\"900\",\"login\":\"viewme\",\"display_name\":\"ViewMe\",\"profile_image_url\":\"\"}]}")
            : (200, "{\"data\":[]}");
        var client = new TwitchClient(MakeFollower.Settings(dir), new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, TwitchEndpoints.FromBase(da.BaseUrl));

        var found = await client.GetUserByLoginAsync("viewme", CancellationToken.None);
        var missing = await client.GetUserByLoginAsync("nobody_here", CancellationToken.None);

        Assert.Equal(new TwitchUser("900", "viewme", "ViewMe", ""), found);
        Assert.Null(missing);
        Assert.StartsWith("GET /helix/users?login=viewme ", da.Requests[0]);
    }
}