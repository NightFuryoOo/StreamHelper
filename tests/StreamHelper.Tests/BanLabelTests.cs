using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class BanEventTests
{
    private static string Note(string type, string ev) =>
        "{\"metadata\":{\"message_id\":\"n1\",\"message_type\":\"notification\",\"message_timestamp\":\"2026-10-16T10:00:00.123456789Z\",\"subscription_type\":\"" + type +
        "\"},\"payload\":{\"event\":" + ev + "}}";

    private static string Ban(string extra) =>
        "{\"user_id\":\"41\",\"user_login\":\"alice\",\"user_name\":\"Alice\",\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\"," +
        "\"moderator_user_id\":\"50\",\"moderator_user_login\":\"sever31ru\",\"moderator_user_name\":\"SeveR31ru\"," + extra + "}";

    [Fact]
    public void A_timeout_says_who_did_it_and_for_how_long()
    {
        var message = EventSubParser.Parse(Note(EventSubParser.BanType,
            Ban("\"reason\":\" спам \",\"banned_at\":\"2026-10-16T10:00:00.123456789Z\",\"ends_at\":\"2026-10-16T10:10:00.123456789Z\",\"is_permanent\":false")));

        Assert.Equal(new ChatModeration(ChatModerationKind.Timeout, "41", "alice", "SeveR31ru", 600, "спам"), message!.Moderation);
        Assert.Null(message.Removal);
        Assert.Null(message.Subscriber);
    }

    [Fact]
    public void A_timeout_a_little_off_the_minute_is_rounded_to_it_and_odd_seconds_stay()
    {
        var almost = EventSubParser.Parse(Note(EventSubParser.BanType,
            Ban("\"banned_at\":\"2026-10-16T10:00:00.9Z\",\"ends_at\":\"2026-10-16T10:09:59.1Z\",\"is_permanent\":false")))!.Moderation!;
        var odd = EventSubParser.Parse(Note(EventSubParser.BanType,
            Ban("\"banned_at\":\"2026-10-16T10:00:00Z\",\"ends_at\":\"2026-10-16T10:00:45Z\",\"is_permanent\":false")))!.Moderation!;

        Assert.Equal(600, almost.Seconds);
        Assert.Equal(45, odd.Seconds);
    }

    [Fact]
    public void A_permanent_ban_has_no_length_and_an_empty_reason_stays_empty()
    {
        var ban = EventSubParser.Parse(Note(EventSubParser.BanType,
            Ban("\"reason\":\"\",\"banned_at\":\"2026-10-16T10:00:00Z\",\"ends_at\":null,\"is_permanent\":true")))!.Moderation!;

        Assert.Equal(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "SeveR31ru"), ban);
    }

    [Fact]
    public void An_unban_says_who_lifted_it_and_the_login_stands_in_for_a_missing_name()
    {
        var lift = EventSubParser.Parse(Note(EventSubParser.UnbanType,
            "{\"user_id\":\"41\",\"user_login\":\"alice\",\"user_name\":\"Alice\",\"broadcaster_user_id\":\"777\",\"moderator_user_id\":\"50\",\"moderator_user_login\":\"sever31ru\",\"moderator_user_name\":\"\"}"))!.Moderation!;

        Assert.Equal(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "sever31ru"), lift);
    }

    [Fact]
    public void A_ban_without_the_viewer_is_ignored()
    {
        Assert.Null(EventSubParser.Parse(Note(EventSubParser.BanType, "{\"broadcaster_user_id\":\"777\",\"is_permanent\":true}"))!.Moderation);
    }

    [Fact]
    public void Ban_events_need_the_new_right_and_are_not_asked_for_as_a_viewer()
    {
        Assert.EndsWith(" channel:moderate", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasBanEventsScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat moderator:manage:banned_users" }.HasBanEventsScope);
        foreach (var type in new[] { EventSubParser.BanType, EventSubParser.UnbanType })
        {
            Assert.True(EventSubParser.IsBanType(type));
            Assert.False(EventSubParser.IsChatType(type));
            Assert.Contains(PingListener.ModerationTypes, t => t.Type == type && t.Scope == TwitchClient.BanEventsScope && t.NotAsViewer);
        }
        Assert.False(EventSubParser.IsBanType(EventSubParser.ChatClearUserType));
    }
}

public class BanSubscriptionTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Ban_events_are_asked_for_the_chosen_channel_without_naming_the_user()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (202, "{}");
        var own = MakeFollower.Settings(dir);
        var client = new TwitchClient(own, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.CreateSubscriptionAsync(EventSubParser.BanType, "S", CancellationToken.None);
        own.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        await client.CreateSubscriptionAsync(EventSubParser.UnbanType, "S", CancellationToken.None);

        Assert.Contains("\"type\":\"channel.ban\"", da.Requests[0]);
        Assert.Contains("\"condition\":{\"broadcaster_user_id\":\"777\"}", da.Requests[0]);
        Assert.Contains("\"type\":\"channel.unban\"", da.Requests[1]);
        Assert.Contains("\"condition\":{\"broadcaster_user_id\":\"555\"}", da.Requests[1]);
    }

    private static async Task<List<string>> SubscribedTypes(Action<AppSettings> setup, List<ChatModeration>? seen = null)
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-B"));
            await Task.Delay(300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", EventSubParser.BanType,
                "{\"user_id\":\"41\",\"user_login\":\"alice\",\"user_name\":\"Alice\",\"moderator_user_name\":\"SeveR31ru\",\"is_permanent\":true}"));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = TwitchClient.Scopes;
        setup(settings.Current);
        var api = new FakeEventSubApi();
        var moderations = seen ?? new List<ChatModeration>();
        var listener = new PingListener(settings, api, server.Url(), _ => Task.CompletedTask, onModeration: m =>
        {
            lock (moderations) moderations.Add(m);
        })
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
        };
        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.Ok));
            await Wait.Until(() =>
            {
                lock (moderations) return moderations.Count > 0;
            }, 2000);
            lock (api.Calls) return api.Calls.Select(c => c.Type).ToList();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task On_the_own_channel_the_listener_asks_for_bans_and_hands_them_on()
    {
        var seen = new List<ChatModeration>();
        var types = await SubscribedTypes(_ => { }, seen);

        Assert.Contains(EventSubParser.BanType, types);
        Assert.Contains(EventSubParser.UnbanType, types);
        lock (seen) Assert.Equal(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "SeveR31ru"), Assert.Single(seen));
    }

    [Fact]
    public async Task As_a_viewer_or_without_the_right_bans_are_not_asked_for()
    {
        var viewer = await SubscribedTypes(s =>
        {
            s.SelectChannel("555", "big_streamer", "Big_Streamer");
            s.TwitchChannelAsViewer = true;
        });
        var oldRights = await SubscribedTypes(s => s.TwitchScopes = TwitchClient.Scopes.Replace(" channel:moderate", ""));
        var moderator = await SubscribedTypes(s => s.SelectChannel("555", "big_streamer", "Big_Streamer"));

        Assert.DoesNotContain(EventSubParser.BanType, viewer);
        Assert.Contains(EventSubParser.ChatClearUserType, viewer);
        Assert.DoesNotContain(EventSubParser.BanType, oldRights);
        Assert.Contains(EventSubParser.BanType, moderator);
    }
}

public class BanFeedTests
{
    private static ChatMessage Msg(string id, string chatterId, string login) =>
        new(id, chatterId, login, login, "text " + id, Array.Empty<ChatMention>(), DateTime.UtcNow);

    private static ChatModeration Mute(string by, int seconds = 600) => new(ChatModerationKind.Timeout, "41", "alice", by, seconds);

    [Fact]
    public void A_mute_labels_what_the_viewer_wrote_before_it_and_not_after()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));
        feed.Push(Msg("b1", "42", "bob"));
        feed.Push(Msg("a2", "", "Alice"));
        var told = new List<(string Id, ChatModeration Moderation)>();
        feed.Moderated += (ids, moderation) => told.AddRange(ids.Select(id => (id, moderation)));

        feed.Moderate(Mute("SeveR31ru"));
        feed.Push(Msg("a3", "41", "alice"));

        Assert.Equal(new[] { "a1", "a2" }, told.Select(t => t.Id));
        Assert.Equal(Mute("SeveR31ru"), feed.ModerationOf("a1"));
        Assert.Null(feed.ModerationOf("b1"));
        Assert.Null(feed.ModerationOf("a3"));
        Assert.Null(feed.ModerationOf(""));
    }

    [Fact]
    public void Twitch_naming_the_same_mute_again_replaces_the_label()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));

        feed.Moderate(Mute("streamer"));
        feed.Moderate(Mute("Streamer"));

        Assert.Equal("Streamer", feed.ModerationOf("a1")!.ModeratorName);
    }

    [Fact]
    public void Lifting_relabels_only_what_was_labelled_and_remembers_what_was_lifted()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));
        feed.Moderate(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "SeveR31ru"));
        feed.Push(Msg("a2", "41", "alice"));
        var told = new List<string>();
        feed.Moderated += (ids, _) => told.AddRange(ids);

        feed.Moderate(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer"));
        feed.Moderate(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer"));

        Assert.Equal(new[] { "a1" }, told);
        Assert.Equal(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer", Lifted: ChatModerationKind.Ban), feed.ModerationOf("a1"));
        Assert.Null(feed.ModerationOf("a2"));
    }

    [Fact]
    public void Lifting_someone_never_labelled_or_after_a_channel_switch_changes_nothing()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));
        feed.Moderate(Mute("SeveR31ru"));
        feed.Clear();
        feed.Push(Msg("a1", "41", "alice"));
        var told = 0;
        feed.Moderated += (_, _) => told++;

        feed.Moderate(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer"));
        feed.Moderate(new ChatModeration(ChatModerationKind.Timeout, "", "", "Streamer", 60));

        Assert.Equal(0, told);
        Assert.Null(feed.ModerationOf("a1"));
    }
}

public class BanLabelTextTests
{
    [Theory]
    [InlineData(600, "мут на 10 мин · SeveR31ru")]
    [InlineData(45, "мут на 45 с · SeveR31ru")]
    [InlineData(7200, "мут на 2 ч · SeveR31ru")]
    [InlineData(86400, "мут на сутки · SeveR31ru")]
    [InlineData(0, "мут · SeveR31ru")]
    public void A_mute_shows_its_length_and_who_gave_it(int seconds, string text)
    {
        Assert.Equal(text, ChatModerationLabel.Text(new ChatModeration(ChatModerationKind.Timeout, "41", "alice", "SeveR31ru", seconds)));
    }

    [Fact]
    public void A_ban_and_a_lift_are_short_and_a_missing_name_is_left_out()
    {
        Assert.Equal("бан · SeveR31ru", ChatModerationLabel.Text(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "SeveR31ru")));
        Assert.Equal("бан", ChatModerationLabel.Text(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "")));
        Assert.Equal("мут снят · Streamer", ChatModerationLabel.Text(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer", Lifted: ChatModerationKind.Timeout)));
        Assert.Equal("бан снят · Streamer", ChatModerationLabel.Text(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer", Lifted: ChatModerationKind.Ban)));
    }

    [Fact]
    public void The_label_on_screen_never_breaks_in_the_middle()
    {
        var unbroken = ChatModerationLabel.Unbroken(new ChatModeration(ChatModerationKind.Timeout, "41", "alice", "SeveR31ru", 600));

        Assert.DoesNotContain(' ', unbroken);
        Assert.Equal("мут на 10 мин · SeveR31ru", unbroken.Replace('\u00A0', ' '));
    }

    [Fact]
    public void The_reason_is_a_hint_only_while_the_mute_or_ban_stands()
    {
        Assert.Equal("Причина: спам", ChatModerationLabel.Tip(new ChatModeration(ChatModerationKind.Ban, "41", "alice", "SeveR31ru", Reason: "спам")));
        Assert.Null(ChatModerationLabel.Tip(new ChatModeration(ChatModerationKind.Timeout, "41", "alice", "SeveR31ru", 60)));
        Assert.Null(ChatModerationLabel.Tip(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "Streamer", Reason: "спам", Lifted: ChatModerationKind.Ban)));
        Assert.True(ChatModerationLabel.IsActive(new ChatModeration(ChatModerationKind.Timeout, "41", "alice", "x", 60)));
        Assert.False(ChatModerationLabel.IsActive(new ChatModeration(ChatModerationKind.Lift, "41", "alice", "x")));
    }
}
