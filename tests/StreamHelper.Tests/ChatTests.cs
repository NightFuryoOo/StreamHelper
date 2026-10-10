using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatMessageParsingTests
{
    private static string Event(string color, string badges) =>
        "{\"broadcaster_user_id\":\"777\",\"chatter_user_id\":\"42\",\"chatter_user_login\":\"viewer\",\"chatter_user_name\":\"Viewer\"," +
        "\"message_id\":\"cm-1\",\"message\":{\"text\":\"привет всем\",\"fragments\":[]}," +
        $"\"color\":{color},\"badges\":{badges},\"message_type\":\"text\"}}";

    [Fact]
    public void The_name_colour_and_the_badges_come_with_the_message()
    {
        var json = MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType,
            Event("\"#1E90FF\"", "[{\"set_id\":\"moderator\",\"id\":\"1\",\"info\":\"\"},{\"set_id\":\"subscriber\",\"id\":\"12\",\"info\":\"14\"}]"));

        var chat = EventSubParser.Parse(json)!.Chat!;

        Assert.Equal("#1E90FF", chat.Color);
        Assert.Equal(new[] { new ChatBadge("moderator", "1"), new ChatBadge("subscriber", "12") }, chat.Badges);
        Assert.Equal("привет всем", chat.Text);
    }

    [Fact]
    public void A_chatter_without_a_colour_or_badges_gets_empty_values()
    {
        var json = MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType, Event("\"\"", "[]"));
        var chat = EventSubParser.Parse(json)!.Chat!;
        Assert.Equal("", chat.Color);
        Assert.Empty(chat.Badges!);

        var missing = MockEventSubServer.Notification("n2", EventSubParser.ChatMessageType,
            "{\"chatter_user_id\":\"1\",\"chatter_user_login\":\"a\",\"chatter_user_name\":\"A\",\"message_id\":\"x\",\"message\":{\"text\":\"t\",\"fragments\":[]}}");
        var other = EventSubParser.Parse(missing)!.Chat!;
        Assert.Equal("", other.Color);
        Assert.Empty(other.Badges!);
    }
}

public class ChatFeedTests
{
    private static ChatMessage Msg(string id, string text = "hi") =>
        new(id, "1", "viewer", "Viewer", text, Array.Empty<ChatMention>(), DateTime.UtcNow);

    [Fact]
    public void Every_message_is_announced_once_even_when_it_arrives_twice()
    {
        var feed = new ChatFeed();
        var seen = new List<string>();
        feed.Message += m => seen.Add(m.MessageId);

        feed.Push(Msg("a"));
        feed.Push(Msg("b"));
        feed.Push(Msg("a"));

        Assert.Equal(new[] { "a", "b" }, seen);
        Assert.Equal(new[] { "a", "b" }, feed.Recent().Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void Only_the_newest_messages_are_kept_for_a_window_opened_later()
    {
        var feed = new ChatFeed();
        for (var i = 0; i < ChatFeed.KeepRecent + 25; i++) feed.Push(Msg("m" + i));

        var recent = feed.Recent();

        Assert.Equal(ChatFeed.KeepRecent, recent.Count);
        Assert.Equal("m25", recent[0].MessageId);
        Assert.Equal("m" + (ChatFeed.KeepRecent + 24), recent[^1].MessageId);
    }
}

public class ChatBadgeTests
{
    private const string Global =
        "{\"data\":[{\"set_id\":\"moderator\",\"versions\":[{\"id\":\"1\",\"image_url_1x\":\"https://cdn/mod1.png\",\"image_url_2x\":\"https://cdn/mod2.png\",\"image_url_4x\":\"https://cdn/mod4.png\"}]}," +
        "{\"set_id\":\"subscriber\",\"versions\":[{\"id\":\"0\",\"image_url_1x\":\"https://cdn/sub-global.png\"}]}]}";

    private const string Channel =
        "{\"data\":[{\"set_id\":\"subscriber\",\"versions\":[{\"id\":\"0\",\"image_url_1x\":\"https://cdn/sub-own.png\",\"image_url_2x\":\"https://cdn/sub-own2.png\"},{\"id\":\"12\",\"image_url_1x\":\"https://cdn/sub12.png\"}]}]}";

    [Fact]
    public void Badge_pictures_are_listed_by_set_and_version_and_prefer_the_2x_size()
    {
        var map = TwitchClient.ParseBadges(Global);

        Assert.Equal("https://cdn/mod2.png", map["moderator/1"]);
        Assert.Equal("https://cdn/sub-global.png", map["subscriber/0"]);
        Assert.Empty(TwitchClient.ParseBadges("{}"));
    }

    [Fact]
    public async Task The_channel_badges_replace_the_global_ones_with_the_same_name()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (req, _) => req.Url!.AbsolutePath.EndsWith("/global") ? (200, Global) : (200, Channel);
        var client = new TwitchClient(MakeFollower.Settings(dir), new System.Net.Http.HttpClient(), TwitchEndpoints.FromBase(server.BaseUrl));

        var map = await client.GetBadgeImagesAsync(CancellationToken.None);

        Assert.Equal("https://cdn/sub-own2.png", map["subscriber/0"]);
        Assert.Equal("https://cdn/sub12.png", map["subscriber/12"]);
        Assert.Equal("https://cdn/mod2.png", map["moderator/1"]);
        Assert.Equal(2, server.Requests.Count);
        Assert.Contains("GET /helix/chat/badges/global", server.Requests[0]);
        Assert.Contains("GET /helix/chat/badges?broadcaster_id=777", server.Requests[1]);
        Assert.Contains("auth=Bearer", server.Requests[0]);
    }

    private sealed class FakeBadgeApi : IBadgeApi
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }

        public Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new System.Net.Http.HttpRequestException("down");
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["moderator/1"] = "https://cdn/mod.png" });
        }
    }

    [Fact]
    public async Task The_catalog_loads_once_and_answers_from_memory()
    {
        var api = new FakeBadgeApi();
        var catalog = new ChatBadgeCatalog(api);
        Assert.Null(catalog.UrlFor("moderator", "1"));

        await catalog.EnsureLoadedAsync();
        await catalog.EnsureLoadedAsync();

        Assert.Equal(1, api.Calls);
        Assert.Equal("https://cdn/mod.png", catalog.UrlFor("moderator", "1"));
        Assert.Null(catalog.UrlFor("vip", "1"));
    }

    private sealed class MapApi : IBadgeApi
    {
        public MapApi(params (string Key, string Url)[] items) => Items = items.ToDictionary(i => i.Key, i => i.Url);
        public Dictionary<string, string> Items { get; }
        public TaskCompletionSource<bool> Gate { get; } = new();

        public async Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct)
        {
            await Gate.Task;
            return Items;
        }
    }

    [Fact]
    public async Task A_missing_step_of_a_series_falls_back_to_the_nearest_lower_one_of_the_same_tier()
    {
        var api = new MapApi(("subscriber/0", "t1-0"), ("subscriber/1012", "t1-12"), ("subscriber/3000", "t3-0"), ("bits/100", "bits100"), ("vip/1", "vip"));
        api.Gate.SetResult(true);
        var catalog = new ChatBadgeCatalog(api);
        await catalog.EnsureLoadedAsync();

        Assert.Equal("t1-12", catalog.UrlFor("subscriber", "1012"));
        Assert.Equal("t1-12", catalog.UrlFor("subscriber", "1014"));
        Assert.Equal("t1-0", catalog.UrlFor("subscriber", "3"));
        Assert.Equal("t3-0", catalog.UrlFor("subscriber", "3014"));
        Assert.Null(catalog.UrlFor("subscriber", "2006"));
        Assert.Equal("bits100", catalog.UrlFor("bits", "100"));
        Assert.Null(catalog.UrlFor("bits", "1"));
        Assert.Null(catalog.UrlFor("vip", "x"));
    }

    [Fact]
    public async Task The_catalog_says_when_it_is_loading_and_announces_when_the_pictures_have_arrived()
    {
        var api = new MapApi(("moderator/1", "mod"));
        var catalog = new ChatBadgeCatalog(api);
        var announced = 0;
        catalog.Loaded += () => Interlocked.Increment(ref announced);
        Assert.False(catalog.IsLoading);

        var load = catalog.EnsureLoadedAsync();
        Assert.True(catalog.IsLoading);
        Assert.False(catalog.IsLoaded);
        Assert.Same(load, catalog.EnsureLoadedAsync());

        api.Gate.SetResult(true);
        await load;

        Assert.False(catalog.IsLoading);
        Assert.True(catalog.IsLoaded);
        Assert.Equal(1, announced);
        await catalog.EnsureLoadedAsync();
        Assert.Equal(1, announced);
    }
    [Fact]
    public async Task A_failed_load_is_not_repeated_at_once_and_does_not_break_the_chat()
    {
        var api = new FakeBadgeApi { Fail = true };
        var catalog = new ChatBadgeCatalog(api);

        await catalog.EnsureLoadedAsync();
        await catalog.EnsureLoadedAsync();
        await catalog.EnsureLoadedAsync();

        Assert.Equal(1, api.Calls);
        Assert.False(catalog.IsLoaded);
        Assert.Null(catalog.UrlFor("moderator", "1"));
    }
}

public class ChatColorTests
{
    private static double Luminance((byte R, byte G, byte B) c)
    {
        static double Lin(double v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    [Fact]
    public void A_bright_colour_is_kept_exactly()
    {
        Assert.Equal(((byte)0x00, (byte)0xFF, (byte)0x7F), ChatColors.Readable("#00FF7F", "viewer"));
        Assert.Equal(((byte)0xFF, (byte)0xFF, (byte)0xFF), ChatColors.Readable("ffffff", "viewer"));
    }

    [Fact]
    public void A_dark_colour_is_lightened_until_it_can_be_read_on_a_dark_background()
    {
        foreach (var dark in new[] { "#0000FF", "#8A2BE2", "#000000", "#B22222" })
        {
            var result = ChatColors.Readable(dark, "viewer");
            Assert.True(Luminance(result) >= 0.34, $"{dark} -> {result} is still too dark");
        }
        var blue = ChatColors.Readable("#0000FF", "viewer");
        Assert.True(blue.B == 255 && blue.R > 0, "the hue is kept, only lightened");
    }

    [Fact]
    public void Without_a_colour_a_name_always_gets_the_same_readable_one()
    {
        var first = ChatColors.Readable("", "Nightfury");
        Assert.Equal(first, ChatColors.Readable(null, "nightfury"));
        Assert.Equal(first, ChatColors.Readable("not a colour", "NIGHTFURY"));
        Assert.True(Luminance(first) >= 0.34);

        var distinct = new[] { "a", "b", "c", "d", "e", "f", "g", "h" }.Select(n => ChatColors.Readable("", n)).Distinct().Count();
        Assert.True(distinct > 3);
    }
}

public class ChatPlacementTests
{
    private static readonly ScreenBounds Work = new(0, 0, 1920, 1040);
    private static readonly ScreenBounds Virtual = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData("BottomRight", 30, 20, 100, 200, 430, 320)]
    [InlineData("TopLeft", -30, -20, 70, 180, 430, 320)]
    [InlineData("TopRight", 30, -20, 100, 180, 430, 320)]
    [InlineData("BottomLeft", -30, 20, 70, 200, 430, 320)]
    public void Every_corner_resizes_and_keeps_the_opposite_corner_in_place(string corner, double dx, double dy, double left, double top, double width, double height)
    {
        Assert.Equal((left, top, width, height), ChatPlacement.Resize(corner, 100, 200, 400, 300, dx, dy));
    }

    [Theory]
    [InlineData("TopLeft")]
    [InlineData("BottomRight")]
    public void A_corner_cannot_shrink_the_chat_below_its_minimum(string corner)
    {
        var shrink = corner == "TopLeft" ? 1000 : -1000;

        var (left, top, width, height) = ChatPlacement.Resize(corner, 100, 200, 400, 300, shrink, shrink);

        Assert.Equal(ChatPlacement.MinWidth, width);
        Assert.Equal(ChatPlacement.MinHeight, height);
        if (corner == "TopLeft")
        {
            Assert.Equal(500, left + width);
            Assert.Equal(500, top + height);
        }
        else
        {
            Assert.Equal(100, left);
            Assert.Equal(200, top);
        }
    }

    [Theory]
    [InlineData(1, "Чат на паузе · новых: 1")]
    [InlineData(25, "Чат на паузе · новых: 25")]
    public void The_paused_strip_counts_waiting_messages(int waiting, string expected)
    {
        Assert.Equal(expected, ChatPlacement.PausedText(waiting));
    }

    [Fact]
    public void Without_a_saved_place_the_chat_sits_in_the_bottom_left_corner()
    {
        var (left, top, width, height) = ChatPlacement.Resolve(null, null, null, null, Work, Virtual);

        Assert.Equal((ChatPlacement.DefaultWidth, ChatPlacement.DefaultHeight), (width, height));
        Assert.Equal(ChatPlacement.Margin, left);
        Assert.Equal(Work.Bottom - ChatPlacement.DefaultHeight - ChatPlacement.Margin, top);
    }

    [Fact]
    public void A_saved_place_is_used_while_it_is_on_a_screen_and_ignored_when_it_is_not()
    {
        var kept = ChatPlacement.Resolve(500, 200, 420, 260, Work, Virtual);
        Assert.Equal((500d, 200d, 420d, 260d), kept);

        var lost = ChatPlacement.Resolve(5000, 200, 420, 260, Work, Virtual);
        Assert.Equal(ChatPlacement.Margin, lost.Left);
        Assert.Equal(Work.Bottom - 260 - ChatPlacement.Margin, lost.Top);
        Assert.Equal(420, lost.Width);
    }

    [Fact]
    public void The_size_is_kept_within_sensible_limits_and_the_settings_are_clamped()
    {
        var small = ChatPlacement.Resolve(null, null, 10, 10, Work, Virtual);
        Assert.Equal((ChatPlacement.MinWidth, ChatPlacement.MinHeight), (small.Width, small.Height));
        var huge = ChatPlacement.Resolve(null, null, 99999, 99999, Work, Virtual);
        Assert.Equal((1920d, 1080d), (huge.Width, huge.Height));

        Assert.Equal(ChatPlacement.MinOpacity, ChatPlacement.ClampOpacity(0));
        Assert.Equal(1.0, ChatPlacement.ClampOpacity(5));
        Assert.Equal(ChatPlacement.DefaultOpacity, ChatPlacement.ClampOpacity(double.NaN));
        Assert.Equal(ChatPlacement.MinFontSize, ChatPlacement.ClampFontSize(1));
        Assert.Equal(ChatPlacement.MaxFontSize, ChatPlacement.ClampFontSize(99));
        Assert.Equal(15, ChatPlacement.ClampFontSize(14.6));
    }
}

public class ChatThroughPingListenerTests
{
    [Fact]
    public async Task Every_chat_message_reaches_the_feed_over_the_same_connection_while_pings_stay_selective()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var mine = ("777", "streamer");
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-C"));
            await Wait.Until(() => false, 300);
            var chat = EventSubParser.ChatMessageType;
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", chat, ChatJson.Event("m2", "43", "bob", "Bob", "просто чат")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", chat, ChatJson.Event("m3", "9", "nightbot", "Nightbot", "@streamer спасибо", mine)));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "user:read:chat";
        var feed = new ChatFeed();
        var pings = new List<string>();
        var listener = new PingListener(settings, new FakeEventSubApi(), server.Url(), items =>
        {
            lock (pings) pings.AddRange(items.Select(p => p.Key));
            return Task.CompletedTask;
        }, feed.Push)
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
        };

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => feed.Recent().Count >= 3));
            await Task.Delay(200);

            Assert.Equal(new[] { "m1", "m2", "m3" }, feed.Recent().Select(m => m.MessageId).ToArray());
            Assert.Equal("#00FF7F", feed.Recent()[0].Color);
            lock (pings) Assert.Equal(new[] { "m1" }, pings.ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_failing_chat_window_never_stops_the_pings()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var mine = ("777", "streamer");
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-F"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType, ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", mine)));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "user:read:chat";
        var pings = new List<string>();
        var listener = new PingListener(settings, new FakeEventSubApi(), server.Url(), items =>
        {
            lock (pings) pings.AddRange(items.Select(p => p.Key));
            return Task.CompletedTask;
        }, _ => throw new InvalidOperationException("window broke"))
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
        };

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => { lock (pings) return pings.Count >= 1; }));
            Assert.Equal(SyncState.Ok, listener.Status.State);
        }
        finally
        {
            listener.Stop();
        }
    }
}

public class ChatModerationTests
{
    private static (TwitchClient Client, SettingsStore Settings) Make(TempDir dir, MockDa server, string scopes = "user:read:chat moderator:manage:banned_users")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return (new TwitchClient(settings, new System.Net.Http.HttpClient(), TwitchEndpoints.FromBase(server.BaseUrl)), settings);
    }

    [Fact]
    public async Task A_time_out_is_a_ban_with_a_length_asked_for_by_the_streamer_as_moderator_of_their_own_channel()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (_, _) => (200, "{\"data\":[{\"user_id\":\"42\"}]}");
        var (client, _) = Make(dir, server);

        var result = await client.BanAsync("42", 600, CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(server.Requests);
        Assert.Contains("POST /helix/moderation/bans?broadcaster_id=777&moderator_id=777", request);
        Assert.Contains("auth=Bearer", request);
        using var body = System.Text.Json.JsonDocument.Parse(request[(request.IndexOf("body=", StringComparison.Ordinal) + 5)..]);
        var data = body.RootElement.GetProperty("data");
        Assert.Equal("42", data.GetProperty("user_id").GetString());
        Assert.Equal(600, data.GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task A_ban_has_no_length_and_lifting_it_deletes_it()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (req, _) => req.HttpMethod == "DELETE" ? (204, "") : (200, "{\"data\":[]}");
        var (client, _) = Make(dir, server);

        Assert.True((await client.BanAsync("42", null, CancellationToken.None)).Success);
        Assert.True((await client.UnbanAsync("42", CancellationToken.None)).Success);

        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain("duration", server.Requests[0]);
        Assert.Contains("DELETE /helix/moderation/bans?broadcaster_id=777&moderator_id=777&user_id=42", server.Requests[1]);
    }

    [Fact]
    public async Task Failures_are_told_in_words_and_never_thrown()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var (client, _) = Make(dir, server);

        server.Handler = (_, _) => (400, "{\"message\":\"The user specified in the user_id field is already banned.\"}");
        Assert.Equal(new ModerationResult(false, "Уже забанен."), await client.BanAsync("42", 60, CancellationToken.None));

        server.Handler = (_, _) => (400, "{\"message\":\"The user specified in the user_id field may not be banned: it is the broadcaster.\"}");
        Assert.Equal("Стримера нельзя.", (await client.BanAsync("777", 60, CancellationToken.None)).Message);

        server.Handler = (_, _) => (400, "{\"message\":\"The user specified in the user_id field is not banned.\"}");
        Assert.Equal("Уже не забанен.", (await client.UnbanAsync("42", CancellationToken.None)).Message);

        server.Handler = (_, _) => (403, "{}");
        Assert.Contains("переподключи", (await client.BanAsync("42", 60, CancellationToken.None)).Message);

        server.Handler = (_, _) => (429, "{}");
        Assert.Contains("Слишком часто", (await client.BanAsync("42", 60, CancellationToken.None)).Message);

        server.Handler = (_, _) => (500, "{}");
        Assert.Contains("500", (await client.BanAsync("42", 60, CancellationToken.None)).Message);

        server.Handler = (_, _) => (400, "{\"message\":\"Something new\"}");
        Assert.Equal("Twitch отказал: Something new", (await client.BanAsync("42", 60, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task Without_the_moderation_right_nothing_is_sent_and_the_streamer_is_told_to_reconnect()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var (client, _) = Make(dir, server, scopes: "user:read:chat");

        var ban = await client.BanAsync("42", 600, CancellationToken.None);
        var lift = await client.UnbanAsync("42", CancellationToken.None);
        var nobody = await Make(dir, server).Client.BanAsync("", 600, CancellationToken.None);

        Assert.False(ban.Success);
        Assert.Contains("переподключить", ban.Message);
        Assert.False(lift.Success);
        Assert.False(nobody.Success);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void The_new_right_is_asked_for_when_connecting_and_recognised_when_present()
    {
        Assert.Contains("moderator:manage:banned_users", TwitchClient.Scopes);
        Assert.Contains("user:read:chat", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasModerationScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat" }.HasModerationScope);
    }

    [Fact]
    public void The_mouse_mode_has_its_own_key_that_opens_no_tab()
    {
        Assert.Contains(HotkeyAction.ChatInteract, HotkeyActions.All);
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.ChatInteract));
        Assert.False(string.IsNullOrWhiteSpace(HotkeyActions.Title(HotkeyAction.ChatInteract)));
        Assert.False(new AppSettings().GetHotkey(HotkeyAction.ChatInteract).IsSet);
    }

    [Fact]
    public void The_quick_mute_is_any_whole_number_of_minutes_from_1_to_100_and_ten_by_default()
    {
        Assert.Equal(10, new AppSettings().ChatMuteMinutes);
        Assert.Equal(10, ChatPlacement.DefaultMuteMinutes);
        Assert.Equal((1, 100), (ChatPlacement.MinMuteMinutes, ChatPlacement.MaxMuteMinutes));

        for (var minutes = 1; minutes <= 100; minutes++) Assert.Equal(minutes, ChatPlacement.ClampMuteMinutes(minutes));
        Assert.Equal(10, ChatPlacement.ClampMuteMinutes(0));
        Assert.Equal(10, ChatPlacement.ClampMuteMinutes(-5));
        Assert.Equal(100, ChatPlacement.ClampMuteMinutes(101));
        Assert.Equal(100, ChatPlacement.ClampMuteMinutes(180));
        Assert.Equal(100, ChatPlacement.ClampMuteMinutes(99999));
        Assert.Equal(37, ChatPlacement.ClampMuteMinutes(37));
    }

    [Fact]
    public void The_chosen_minutes_are_worded_for_the_slider_and_the_button()
    {
        Assert.Equal("1 мин", ChatPlacement.DescribeDuration(1 * 60));
        Assert.Equal("37 мин", ChatPlacement.DescribeDuration(37 * 60));
        Assert.Equal("1 ч", ChatPlacement.DescribeDuration(60 * 60));
        Assert.Equal("90 мин", ChatPlacement.DescribeDuration(90 * 60));
        Assert.Equal("100 мин", ChatPlacement.DescribeDuration(100 * 60));
    }
}

public class MuteDurationTests
{
    [Fact]
    public void The_typed_number_and_the_unit_make_the_seconds_twitch_wants()
    {
        Assert.True(MuteDuration.TryCompute("25", DurationUnit.Minutes, out var s, out _));
        Assert.Equal(1500, s);
        Assert.True(MuteDuration.TryCompute(" 2 ", DurationUnit.Hours, out s, out _));
        Assert.Equal(7200, s);
        Assert.True(MuteDuration.TryCompute("3", DurationUnit.Days, out s, out _));
        Assert.Equal(259200, s);
        Assert.True(MuteDuration.TryCompute("45", DurationUnit.Seconds, out s, out _));
        Assert.Equal(45, s);
        Assert.True(MuteDuration.TryCompute("1", DurationUnit.Seconds, out s, out _));
        Assert.Equal(1, s);
    }

    [Fact]
    public void Two_weeks_is_the_longest_and_nothing_empty_or_zero_or_odd_goes_through()
    {
        Assert.True(MuteDuration.TryCompute("14", DurationUnit.Days, out var s, out _));
        Assert.Equal(MuteDuration.MaxSeconds, s);
        Assert.True(MuteDuration.TryCompute("20160", DurationUnit.Minutes, out s, out _));
        Assert.Equal(MuteDuration.MaxSeconds, s);

        foreach (var (text, unit) in new[] { ("15", DurationUnit.Days), ("20161", DurationUnit.Minutes), ("9999999", DurationUnit.Days), ("0", DurationUnit.Minutes), ("00", DurationUnit.Hours) })
        {
            Assert.False(MuteDuration.TryCompute(text, unit, out s, out var error), text + " " + unit);
            Assert.Equal(MuteDuration.Limits, error);
            Assert.Equal(0, s);
        }
        Assert.False(MuteDuration.TryCompute("", DurationUnit.Minutes, out _, out var empty));
        Assert.Equal("Впиши время.", empty);
        Assert.False(MuteDuration.TryCompute(null, DurationUnit.Minutes, out _, out _));
        foreach (var odd in new[] { "-5", "1.5", "10m", "abc", "1 0", "٣" })
        {
            Assert.False(MuteDuration.TryCompute(odd, DurationUnit.Minutes, out _, out var oddError), odd);
            Assert.Equal("Только цифры.", oddError);
        }
        Assert.False(MuteDuration.TryCompute("99999999999999999999", DurationUnit.Seconds, out _, out _));
    }

    [Fact]
    public void A_mute_length_is_said_in_the_largest_whole_unit()
    {
        Assert.Equal("45 с", ChatPlacement.DescribeDuration(45));
        Assert.Equal("1 мин", ChatPlacement.DescribeDuration(60));
        Assert.Equal("10 мин", ChatPlacement.DescribeDuration(600));
        Assert.Equal("90 мин", ChatPlacement.DescribeDuration(5400));
        Assert.Equal("1 ч", ChatPlacement.DescribeDuration(3600));
        Assert.Equal("2 ч", ChatPlacement.DescribeDuration(7200));
        Assert.Equal("сутки", ChatPlacement.DescribeDuration(86400));
        Assert.Equal("3 сут", ChatPlacement.DescribeDuration(259200));
        Assert.Equal("14 сут", ChatPlacement.DescribeDuration(MuteDuration.MaxSeconds));
        Assert.Equal("65 с", ChatPlacement.DescribeDuration(65));
    }

    [Fact]
    public void The_hide_and_show_key_is_its_own_action_that_opens_no_tab()
    {
        Assert.Contains(HotkeyAction.ChatToggle, HotkeyActions.All);
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.ChatToggle));
        Assert.Equal("Чат: показать / скрыть", HotkeyActions.Title(HotkeyAction.ChatToggle));
        Assert.False(new AppSettings().GetHotkey(HotkeyAction.ChatToggle).IsSet);
        Assert.NotEqual(HotkeyActions.Title(HotkeyAction.ChatToggle), HotkeyActions.Title(HotkeyAction.ChatInteract));
    }
}
