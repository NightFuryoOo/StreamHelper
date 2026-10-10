using System.Net.WebSockets;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal static class ChatJson
{
    public static string Event(string messageId, string chatterId, string chatterLogin, string chatterName, string text,
        params (string Id, string Login)[] mentions)
    {
        var fragments = new List<string> { "{\"type\":\"text\",\"text\":\"" + text + "\",\"cheermote\":null,\"emote\":null,\"mention\":null}" };
        foreach (var (id, login) in mentions)
        {
            fragments.Add("{\"type\":\"mention\",\"text\":\"@" + login + "\",\"cheermote\":null,\"emote\":null,\"mention\":{\"user_id\":\"" + id + "\",\"user_name\":\"" + login + "\",\"user_login\":\"" + login + "\"}}");
        }
        return "{\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\"," +
               "\"chatter_user_id\":\"" + chatterId + "\",\"chatter_user_login\":\"" + chatterLogin + "\",\"chatter_user_name\":\"" + chatterName + "\"," +
               "\"message_id\":\"" + messageId + "\",\"message\":{\"text\":\"" + text + "\",\"fragments\":[" + string.Join(",", fragments) + "]}," +
               "\"color\":\"#00FF7F\",\"badges\":[],\"message_type\":\"text\",\"cheer\":null,\"reply\":null,\"channel_points_custom_reward_id\":null}";
    }

    public static ChatMessage Message(string chatterId, string chatterLogin, string text, params (string Id, string Login)[] mentions) =>
        EventSubParser.Parse(MockEventSubServer.Notification("n", EventSubParser.ChatMessageType,
            Event("m-" + chatterLogin, chatterId, chatterLogin, chatterLogin, text, mentions)))!.Chat!;
}

public class ChatParserTests
{
    [Fact]
    public void A_chat_message_is_parsed_with_its_sender_text_and_mentions()
    {
        var json = MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType,
            ChatJson.Event("cm-1", "42", "viewer", "Viewer", "@streamer hello there", ("777", "streamer"), ("5", "friend")));

        var message = EventSubParser.Parse(json)!;

        Assert.Equal("notification", message.Type);
        Assert.Null(message.Subscriber);
        Assert.Null(message.Redemption);
        var chat = Assert.IsType<ChatMessage>(message.Chat);
        Assert.Equal("cm-1", chat.MessageId);
        Assert.Equal(("42", "viewer", "Viewer"), (chat.ChatterId, chat.ChatterLogin, chat.ChatterName));
        Assert.Equal("@streamer hello there", chat.Text);
        Assert.Equal(new[] { "777", "5" }, chat.Mentions.Select(m => m.UserId).ToArray());
        Assert.Equal(new[] { "streamer", "friend" }, chat.Mentions.Select(m => m.Login).ToArray());
    }

    [Fact]
    public void A_message_without_fragments_or_text_still_parses_and_other_events_carry_no_chat()
    {
        var bare = EventSubParser.Parse(MockEventSubServer.Notification("n2", EventSubParser.ChatMessageType,
            "{\"chatter_user_id\":\"1\",\"chatter_user_login\":\"a\",\"chatter_user_name\":\"A\"}"))!;
        Assert.NotNull(bare.Chat);
        Assert.Equal("", bare.Chat!.Text);
        Assert.Empty(bare.Chat.Mentions);
        Assert.Equal("n2", bare.Chat.MessageId);

        var sub = EventSubParser.Parse(MockEventSubServer.Notification("n3", "channel.subscribe", MockEventSubServer.NewSubEvent("x", "X")))!;
        Assert.Null(sub.Chat);
        Assert.NotNull(sub.Subscriber);
    }
}

public class PingDetectorTests
{
    private static readonly string[] Bots = { "nightbot", "streamelements" };

    private static bool Ping(ChatMessage message, string id = "777", string login = "streamer") =>
        PingDetector.IsPing(message, id, login, Bots);

    [Fact]
    public void A_mention_marked_by_twitch_is_a_ping_by_id_or_by_login()
    {
        Assert.True(Ping(ChatJson.Message("42", "viewer", "@streamer hi", ("777", "streamer"))));
        Assert.True(Ping(ChatJson.Message("42", "viewer", "hi @someone", ("777", "other-name"))));
        Assert.True(Ping(ChatJson.Message("42", "viewer", "hi", ("0", "STREAMER"))));
    }

    [Fact]
    public void The_text_is_a_fallback_when_twitch_did_not_mark_the_mention()
    {
        Assert.True(Ping(ChatJson.Message("42", "viewer", "эй @streamer, ты тут?")));
        Assert.True(Ping(ChatJson.Message("42", "viewer", "@STREAMER")));
        Assert.True(Ping(ChatJson.Message("42", "viewer", "привет всем и @Streamer!")));
        Assert.True(Ping(ChatJson.Message("42", "viewer", "(@streamer)")));
    }

    [Fact]
    public void Only_an_at_sign_name_counts_not_the_bare_name_nor_a_longer_name()
    {
        Assert.False(Ping(ChatJson.Message("42", "viewer", "streamer, привет")));
        Assert.False(Ping(ChatJson.Message("42", "viewer", "hi @streamerfan")));
        Assert.False(Ping(ChatJson.Message("42", "viewer", "hi @streamer_2")));
        Assert.False(Ping(ChatJson.Message("42", "viewer", "mail me@streamer")));
        Assert.False(Ping(ChatJson.Message("42", "viewer", "hi @someone", ("5", "someone"))));
        Assert.False(Ping(ChatJson.Message("42", "viewer", "просто сообщение")));
    }

    [Fact]
    public void The_streamers_own_messages_never_count()
    {
        Assert.False(Ping(ChatJson.Message("777", "streamer", "@streamer note to self", ("777", "streamer"))));
        Assert.False(Ping(ChatJson.Message("0", "Streamer", "@streamer", ("777", "streamer"))));
    }

    [Fact]
    public void Bots_are_ignored_in_any_letter_case_and_other_viewers_are_not()
    {
        Assert.False(Ping(ChatJson.Message("9", "nightbot", "@streamer thanks for following", ("777", "streamer"))));
        Assert.False(Ping(ChatJson.Message("9", "StreamElements", "@streamer", ("777", "streamer"))));
        Assert.True(Ping(ChatJson.Message("9", "nightbotfan", "@streamer", ("777", "streamer"))));
    }

    [Fact]
    public void Without_any_identity_nothing_is_a_ping()
    {
        var message = ChatJson.Message("42", "viewer", "@streamer", ("777", "streamer"));

        Assert.False(PingDetector.IsPing(message, "", "", Bots));
    }

    [Fact]
    public void The_login_is_matched_by_id_alone_when_the_login_is_unknown()
    {
        var message = ChatJson.Message("42", "viewer", "hi", ("777", "streamer"));

        Assert.True(PingDetector.IsPing(message, "777", "", Bots));
        Assert.False(PingDetector.IsPing(message, "888", "", Bots));
    }

    [Theory]
    [InlineData("nightbot, @StreamElements ; moobot", "nightbot,streamelements,moobot")]
    [InlineData("  a  b\nc,,d  ", "a,b,c,d")]
    [InlineData("Bot BOT bot", "bot")]
    [InlineData("", "")]
    [InlineData(" , ; ", "")]
    public void The_ignored_list_is_read_leniently(string text, string expected) =>
        Assert.Equal(expected, string.Join(",", PingDetector.ParseIgnoredList(text)));

    [Fact]
    public void The_default_list_has_the_common_bots_in_lower_case()
    {
        Assert.Contains("nightbot", AppSettings.DefaultPingIgnored);
        Assert.Contains("streamelements", AppSettings.DefaultPingIgnored);
        Assert.All(AppSettings.DefaultPingIgnored, name => Assert.Equal(name.ToLowerInvariant(), name));
        Assert.Equal(AppSettings.DefaultPingIgnored, new AppSettings().PingIgnoredChatters);
    }
}

public class PingStoreAndSettingsTests
{
    private static ChatPing Ping(string key, int minutes, string text = "@streamer hi") =>
        new() { Key = key, Login = "viewer", DisplayName = "Viewer", Message = text, AtUtc = MakeFollower.T0.AddMinutes(minutes) };

    [Fact]
    public void Pings_are_kept_newest_first_counted_as_unseen_and_survive_a_restart()
    {
        using var dir = new TempDir();
        var store = new PingStore(dir.File("pings.json"));

        store.AddRange(new[] { Ping("b", 2), Ping("a", 1), Ping("c", 3) });
        store.Items.First(p => p.Key == "a").Seen = true;

        Assert.Equal(new[] { "c", "b", "a" }, store.Items.Select(p => p.Key).ToArray());
        Assert.Equal(2, store.UnseenCount);

        var reloaded = new PingStore(dir.File("pings.json"));
        Assert.Equal(new[] { "c", "b", "a" }, reloaded.Items.Select(p => p.Key).ToArray());
        Assert.Equal(2, reloaded.UnseenCount);
        Assert.True(reloaded.Items.First(p => p.Key == "a").Seen);
        Assert.Equal("@streamer hi", reloaded.Items[0].Message);
    }

    [Fact]
    public void The_same_chat_message_is_stored_once_and_a_deleted_ping_can_be_restored()
    {
        using var dir = new TempDir();
        var store = new PingStore(dir.File("pings.json"));
        store.AddRange(new[] { Ping("a", 1) });

        Assert.Empty(store.AddRange(new[] { Ping("a", 1) }));
        var ping = store.Items[0];
        var index = store.Remove(ping);
        Assert.Empty(store.Items);
        store.Restore(ping, index);

        Assert.Single(store.Items);
    }

    [Fact]
    public void Several_pings_are_removed_with_one_save_and_come_back_in_their_places()
    {
        using var dir = new TempDir();
        var store = new PingStore(dir.File("pings.json"));
        store.AddRange(new[] { Ping("a", 1), Ping("b", 2), Ping("c", 3), Ping("d", 4), Ping("e", 5), Ping("f", 6) });
        var order = store.Items.Select(p => p.Key).ToArray();
        var picked = new[] { store.Items.First(p => p.Key == "d"), store.Items.First(p => p.Key == "a"), store.Items.First(p => p.Key == "e") };

        var removed = store.RemoveRange(picked);

        Assert.Equal(3, removed.Count);
        Assert.Equal(new[] { "f", "c", "b" }, store.Items.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "f", "c", "b" }, new PingStore(dir.File("pings.json")).Items.Select(p => p.Key).ToArray());
        Assert.Equal(3, store.UnseenCount);

        store.RestoreRange(removed);

        Assert.Equal(order, store.Items.Select(p => p.Key).ToArray());
        Assert.Equal(order, new PingStore(dir.File("pings.json")).Items.Select(p => p.Key).ToArray());
        Assert.Equal(6, store.UnseenCount);
    }

    [Fact]
    public void Removing_a_range_skips_pings_that_are_already_gone_and_restoring_never_duplicates()
    {
        using var dir = new TempDir();
        var store = new PingStore(dir.File("pings.json"));
        store.AddRange(new[] { Ping("a", 1), Ping("b", 2) });
        var a = store.Items.First(p => p.Key == "a");

        store.Remove(a);
        var removed = store.RemoveRange(new[] { a, store.Items[0] });

        Assert.Single(removed);
        store.Restore(removed[0].Item, removed[0].Index);
        store.RestoreRange(removed);
        Assert.Single(store.Items);
    }

    [Fact]
    public void A_ticked_ping_is_not_saved_and_ticking_it_does_not_rewrite_the_file()
    {
        using var dir = new TempDir();
        var path = dir.File("pings.json");
        var store = new PingStore(path);
        store.AddRange(new[] { Ping("a", 1) });
        var before = System.IO.File.GetLastWriteTimeUtc(path);
        System.Threading.Thread.Sleep(30);

        store.Items[0].Selected = true;

        Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(path));
        Assert.DoesNotContain("Selected", System.IO.File.ReadAllText(path));
        Assert.False(new PingStore(path).Items[0].Selected);
    }

    [Fact]
    public void A_ping_card_shows_a_name_a_time_and_whether_it_has_text()
    {
        var ping = Ping("a", 0);
        Assert.Equal("Viewer", ping.Name);
        Assert.True(ping.HasMessage);
        Assert.True(ping.IsNew);
        Assert.Matches(@"^\d\d\.\d\d \d\d:\d\d$", ping.TimeText);

        Assert.Equal("viewer", new ChatPing { Login = "viewer" }.Name);
        Assert.Equal("Без имени", new ChatPing().Name);
        Assert.False(new ChatPing { Message = "  " }.HasMessage);
    }

    [Fact]
    public void The_ping_options_default_on_and_survive_a_restart_and_a_broken_file()
    {
        Assert.True(new AppSettings().NotifyPings);

        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.NotifyPings = false;
        store.Current.PingIgnoredChatters = new List<string> { "mybot" };
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;
        Assert.False(reloaded.NotifyPings);
        Assert.Equal(new[] { "mybot" }, reloaded.PingIgnoredChatters.ToArray());

        File.WriteAllText(dir.File("old.json"), "{\"ShowToast\":false}");
        var old = new SettingsStore(dir.File("old.json")).Current;
        Assert.True(old.NotifyPings);
        Assert.Contains("nightbot", old.PingIgnoredChatters);
    }
}

public class PingListenerTests
{
    private static SettingsStore Settings(TempDir dir, string scopes)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private static PingListener NewListener(SettingsStore settings, FakeEventSubApi api, string url, List<ChatPing> delivered) =>
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

    private static int Count(List<ChatPing> list)
    {
        lock (list) return list.Count;
    }

    [Fact]
    public async Task Listens_to_the_chat_and_delivers_only_real_pings_once_each()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var mine = ("777", "streamer");
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-P"));
            await Wait.Until(() => false, 300);
            var chat = EventSubParser.ChatMessageType;
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "42", "alice", "Alice", "@streamer привет", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", chat, ChatJson.Event("m2", "43", "bob", "Bob", "просто чат")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", chat, ChatJson.Event("m3", "9", "nightbot", "Nightbot", "@streamer спасибо", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n4", chat, ChatJson.Event("m4", "777", "streamer", "Streamer", "@streamer", mine)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n5", chat, ChatJson.Event("m5", "44", "carol", "Carol", "эй @Streamer")));
            await Task.Delay(Timeout.Infinite);
        };
        var api = new FakeEventSubApi();
        var delivered = new List<ChatPing>();
        var listener = NewListener(Settings(dir, "user:read:chat"), api, server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) >= 2));
            await Task.Delay(300);

            Assert.Equal(new[] { EventSubParser.ChatMessageType, EventSubParser.ChatNoticeType }, api.Calls.Select(c => c.Type).ToArray());
            Assert.All(api.Calls, c => Assert.Equal("SESS-P", c.SessionId));
            Assert.Equal(2, Count(delivered));
            lock (delivered)
            {
                Assert.Equal(new[] { "m1", "m5" }, delivered.Select(p => p.Key).ToArray());
                Assert.Equal(("alice", "Alice", "@streamer привет"), (delivered[0].Login, delivered[0].DisplayName, delivered[0].Message));
            }
            Assert.Equal(SyncState.Ok, listener.Status.State);
            Assert.StartsWith("Подключено · ", listener.Status.Message);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_name_added_to_the_ignored_list_takes_effect_without_a_restart()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var chat = EventSubParser.ChatMessageType;
        var settings = Settings(dir, "user:read:chat");
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-I"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "50", "mybot", "MyBot", "@streamer first", ("777", "streamer"))));
            await Wait.Until(() => false, 400);
            settings.Current.PingIgnoredChatters = new List<string> { "mybot" };
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", chat, ChatJson.Event("m2", "50", "mybot", "MyBot", "@streamer second", ("777", "streamer"))));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", chat, ChatJson.Event("m3", "51", "human", "Human", "@streamer third", ("777", "streamer"))));
            await Task.Delay(Timeout.Infinite);
        };
        var delivered = new List<ChatPing>();
        var listener = NewListener(settings, new FakeEventSubApi(), server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) >= 2));
            await Task.Delay(300);

            lock (delivered) Assert.Equal(new[] { "m1", "m3" }, delivered.Select(p => p.Key).ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Without_the_chat_right_it_asks_to_reconnect_and_does_not_open_a_socket()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var api = new FakeEventSubApi();
        var listener = NewListener(Settings(dir, "moderator:read:followers channel:read:subscriptions channel:read:redemptions"), api, server.Url(), new List<ChatPing>());

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => listener.Status.State == SyncState.NeedsLogin));
            Assert.Contains("пинг", listener.Status.Message);
            Assert.Equal(0, server.ConnectionCount);
            Assert.Equal(0, api.CallCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_null_ignored_list_in_a_hand_edited_file_falls_back_to_the_default_bots()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        var chat = EventSubParser.ChatMessageType;
        var settings = Settings(dir, "user:read:chat");
        settings.Current.PingIgnoredChatters = null!;
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-N"));
            await Wait.Until(() => false, 300);
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", chat, ChatJson.Event("m1", "9", "nightbot", "Nightbot", "@streamer", ("777", "streamer"))));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", chat, ChatJson.Event("m2", "60", "human", "Human", "@streamer", ("777", "streamer"))));
            await Task.Delay(Timeout.Infinite);
        };
        var delivered = new List<ChatPing>();
        var listener = NewListener(settings, new FakeEventSubApi(), server.Url(), delivered);

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => Count(delivered) >= 1));
            await Task.Delay(300);

            lock (delivered) Assert.Equal(new[] { "m2" }, delivered.Select(p => p.Key).ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }
}