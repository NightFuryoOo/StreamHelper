using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatRemovalTests
{
    private static string Note(string type, string ev) =>
        "{\"metadata\":{\"message_id\":\"n1\",\"message_type\":\"notification\",\"message_timestamp\":\"2026-10-11T00:00:00Z\",\"subscription_type\":\"" + type +
        "\"},\"payload\":{\"event\":" + ev + "}}";

    private static ChatMessage Msg(string id, string chatterId, string login) =>
        new(id, chatterId, login, login, "text " + id, Array.Empty<ChatMention>(), DateTime.UtcNow);

    [Fact]
    public void A_message_deleted_on_twitch_comes_with_its_id_and_author()
    {
        var message = EventSubParser.Parse(Note("channel.chat.message_delete",
            "{\"broadcaster_user_id\":\"777\",\"target_user_id\":\"41\",\"target_user_login\":\"alice\",\"target_user_name\":\"Alice\",\"message_id\":\"m-9\"}"));

        Assert.Equal(new ChatRemoval(ChatRemovalKind.Message, "m-9", "41", "alice"), message!.Removal);
        Assert.Null(message.Chat);
    }

    [Fact]
    public void A_ban_or_timeout_clears_everything_the_viewer_wrote_and_slash_clear_clears_the_chat()
    {
        var user = EventSubParser.Parse(Note("channel.chat.clear_user_messages",
            "{\"broadcaster_user_id\":\"777\",\"target_user_id\":\"41\",\"target_user_login\":\"alice\",\"target_user_name\":\"Alice\"}"));
        var all = EventSubParser.Parse(Note("channel.chat.clear", "{\"broadcaster_user_id\":\"777\"}"));

        Assert.Equal(new ChatRemoval(ChatRemovalKind.User, "", "41", "alice"), user!.Removal);
        Assert.Equal(ChatRemovalKind.All, all!.Removal!.Kind);
    }

    [Fact]
    public void A_deletion_without_a_message_id_is_ignored()
    {
        Assert.Null(EventSubParser.Parse(Note("channel.chat.message_delete", "{\"broadcaster_user_id\":\"777\"}"))!.Removal);
        Assert.Null(EventSubParser.Parse(Note("channel.chat.clear_user_messages", "{\"broadcaster_user_id\":\"777\"}"))!.Removal);
    }

    [Fact]
    public void Deletion_events_use_the_same_chat_condition_as_chat_messages()
    {
        foreach (var type in new[] { EventSubParser.ChatDeleteType, EventSubParser.ChatClearUserType, EventSubParser.ChatClearType })
        {
            Assert.True(EventSubParser.IsChatType(type));
            Assert.Contains(PingListener.RemovalTypes, t => t.Type == type && t.Scope == TwitchClient.ChatScope);
        }
        Assert.False(EventSubParser.IsChatType("channel.subscribe"));
    }

    [Fact]
    public void Clearing_a_viewer_marks_only_their_messages_once_and_tells_the_windows()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));
        feed.Push(Msg("b1", "42", "bob"));
        feed.Push(Msg("a2", "", "Alice"));
        var told = new List<string>();
        feed.Deleted += ids => told.AddRange(ids);

        feed.Remove(new ChatRemoval(ChatRemovalKind.User, UserId: "41", UserLogin: "alice"));
        feed.Remove(new ChatRemoval(ChatRemovalKind.User, UserId: "41", UserLogin: "alice"));

        Assert.Equal(new[] { "a1", "a2" }, told);
        Assert.False(feed.IsDeleted("b1"));
        feed.Push(Msg("a3", "41", "alice"));
        Assert.False(feed.IsDeleted("a3"));
    }

    [Fact]
    public void Clearing_the_chat_marks_every_message_and_a_single_deletion_is_told_once()
    {
        var feed = new ChatFeed();
        feed.Push(Msg("a1", "41", "alice"));
        feed.Push(Msg("b1", "42", "bob"));
        var told = new List<string>();
        feed.Deleted += ids => told.AddRange(ids);

        feed.Remove(new ChatRemoval(ChatRemovalKind.Message, "a1"));
        feed.MarkDeleted("a1");
        feed.Remove(new ChatRemoval(ChatRemovalKind.All));

        Assert.Equal(new[] { "a1", "b1" }, told);
    }
}

public class ChatReplyTests
{
    private static string Chat(string extra) =>
        "{\"metadata\":{\"message_id\":\"n1\",\"message_type\":\"notification\",\"message_timestamp\":\"2026-10-11T00:00:00Z\",\"subscription_type\":\"channel.chat.message\"}," +
        "\"payload\":{\"event\":{\"broadcaster_user_id\":\"777\",\"chatter_user_id\":\"42\",\"chatter_user_login\":\"bob\",\"chatter_user_name\":\"Bob\",\"message_id\":\"m2\"," +
        "\"message\":{\"text\":\"@Alice да\",\"fragments\":[]}" + extra + "}}}";

    [Fact]
    public void A_reply_knows_whom_and_what_it_answers()
    {
        var chat = EventSubParser.Parse(Chat(",\"reply\":{\"parent_message_id\":\"m1\",\"parent_message_body\":\"кто тут?\",\"parent_user_id\":\"41\",\"parent_user_name\":\"Alice\",\"parent_user_login\":\"alice\",\"thread_message_id\":\"m1\"}"))!.Chat!;

        Assert.Equal(new ChatReply("m1", "alice", "Alice", "кто тут?"), chat.Reply);
        Assert.Equal("Alice", chat.Reply!.ParentLabel);
    }

    [Fact]
    public void An_ordinary_message_or_an_empty_reply_has_no_quote()
    {
        Assert.Null(EventSubParser.Parse(Chat(""))!.Chat!.Reply);
        Assert.Null(EventSubParser.Parse(Chat(",\"reply\":null"))!.Chat!.Reply);
        Assert.Null(EventSubParser.Parse(Chat(",\"reply\":{\"parent_message_id\":\"m1\"}"))!.Chat!.Reply);
    }

    [Fact]
    public void The_quote_is_a_bit_smaller_than_the_chat_text()
    {
        Assert.Equal(13.6, ChatOverlayWindow.ReplySize(16));
        Assert.True(ChatOverlayWindow.ReplySize(14) < 14);
    }
}

public class ChattersTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public void The_login_asks_for_the_right_to_see_who_is_in_the_chat_last()
    {
        Assert.EndsWith(" moderator:read:chatters", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasChattersScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat" }.HasChattersScope);
    }

    [Fact]
    public void The_chatters_list_is_read_with_its_cursor()
    {
        var (items, cursor) = TwitchClient.ParseChatters(
            "{\"data\":[{\"user_id\":\"1\",\"user_login\":\"alice\",\"user_name\":\"Alice\"},{\"user_id\":\"2\",\"user_login\":\"\",\"user_name\":\"x\"}],\"pagination\":{\"cursor\":\"c2\"},\"total\":2}");

        Assert.Equal(new[] { new ChatterInfo("1", "alice", "Alice") }, items);
        Assert.Equal("c2", cursor);
    }

    [Fact]
    public async Task All_pages_of_the_chosen_channel_are_asked_as_its_moderator()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (request, _) => request.QueryString["after"] == null
            ? (200, "{\"data\":[{\"user_id\":\"1\",\"user_login\":\"alice\",\"user_name\":\"Alice\"}],\"pagination\":{\"cursor\":\"p2\"}}")
            : (200, "{\"data\":[{\"user_id\":\"2\",\"user_login\":\"bob\",\"user_name\":\"Bob\"}],\"pagination\":{}}");
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = TwitchClient.Scopes;
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var chatters = await client.GetChattersAsync(CancellationToken.None);

        Assert.Equal(new[] { "alice", "bob" }, chatters.Select(c => c.Login).ToArray());
        Assert.StartsWith("GET /helix/chat/chatters?broadcaster_id=555&moderator_id=777&first=1000 ", da.Requests[0]);
        Assert.StartsWith("GET /helix/chat/chatters?broadcaster_id=555&moderator_id=777&first=1000&after=p2 ", da.Requests[1]);
    }

    [Fact]
    public async Task Without_the_right_nothing_is_asked()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "user:read:chat";
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetChattersAsync(CancellationToken.None));
        Assert.Empty(da.Requests);
    }

    [Fact]
    public void People_who_wrote_come_first_then_everyone_else_in_the_chat_by_name()
    {
        var recent = new RecentChatters();
        recent.Saw("zed", "Zed", "#FF0000");
        recent.Saw("anna", "Anna", "#00FF00");
        var everyone = new[] { new Chatter("bob", "Bob", ""), new Chatter("ANNA", "Anna", ""), new Chatter("streamer", "Streamer", ""), new Chatter("aaron", "Aaron", "") };

        var all = recent.Match("", "streamer", 10, everyone: everyone);
        var a = recent.Match("a", "streamer", 10, everyone: everyone);

        Assert.Equal(new[] { "anna", "zed", "aaron", "bob" }, all.Select(c => c.Login).ToArray());
        Assert.Equal("#00FF00", all[0].Color);
        Assert.Equal(new[] { "anna", "aaron" }, a.Select(c => c.Login).ToArray());
    }

    private sealed class FakeChatters : IChattersApi
    {
        public int Calls;
        public bool Fail;

        public Task<IReadOnlyList<ChatterInfo>> GetChattersAsync(CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyList<ChatterInfo>>(new[] { new ChatterInfo("1", "alice", "Alice"), new ChatterInfo("2", "bob", "") });
        }
    }

    [Fact]
    public async Task The_list_is_kept_for_a_while_and_dropped_when_the_channel_changes()
    {
        var api = new FakeChatters();
        var directory = new ChatterDirectory(api, () => true);

        await directory.RefreshAsync();
        await directory.RefreshAsync();

        Assert.Equal(1, api.Calls);
        Assert.Equal(new[] { new Chatter("alice", "Alice", ""), new Chatter("bob", "bob", "") }, directory.All);
        directory.Reset();
        Assert.Empty(directory.All);
        await directory.RefreshAsync();
        Assert.Equal(2, api.Calls);
    }

    [Fact]
    public async Task Without_the_right_or_after_a_failure_it_does_not_ask_again_at_once()
    {
        var api = new FakeChatters();
        await new ChatterDirectory(api, () => false).RefreshAsync();
        Assert.Equal(0, api.Calls);

        api.Fail = true;
        var directory = new ChatterDirectory(api, () => true);
        await directory.RefreshAsync();
        await directory.RefreshAsync();
        Assert.Equal(1, api.Calls);
        Assert.Empty(directory.All);
    }
}

public class ExtraEmoteSourceTests
{
    [Fact]
    public void Bttv_global_emotes_get_gif_or_png_pictures_and_modifiers_are_skipped()
    {
        var emotes = BttvClient.ParseGlobal(
            "[{\"id\":\"a1\",\"code\":\"catJAM\",\"imageType\":\"gif\",\"animated\":true},{\"id\":\"s1\",\"code\":\"FeelsGoodMan\",\"imageType\":\"png\",\"animated\":false},{\"id\":\"m1\",\"code\":\"w!\",\"imageType\":\"png\",\"modifier\":true}]");

        Assert.Equal(new[]
        {
            new ExtraEmote("catJAM", "https://cdn.betterttv.net/emote/a1/2x.gif", true, ExtraSource.Bttv),
            new ExtraEmote("FeelsGoodMan", "https://cdn.betterttv.net/emote/s1/2x.png", false, ExtraSource.Bttv),
        }, emotes);
    }

    [Fact]
    public void Bttv_channel_emotes_are_the_channels_own_and_the_shared_ones()
    {
        var emotes = BttvClient.ParseChannel(
            "{\"id\":\"x\",\"channelEmotes\":[{\"id\":\"c1\",\"code\":\"chanOne\",\"imageType\":\"png\"}],\"sharedEmotes\":[{\"id\":\"s1\",\"code\":\"sharedGif\",\"imageType\":\"gif\"}]}", "http://cdn");

        Assert.Equal(new[] { "chanOne", "sharedGif" }, emotes.Select(e => e.Name).ToArray());
        Assert.Equal("http://cdn/emote/s1/2x.gif", emotes[1].Url);
        Assert.True(emotes[1].Animated);
    }

    [Fact]
    public void Ffz_global_emotes_come_only_from_the_default_sets_and_hidden_ones_are_skipped()
    {
        var emotes = FfzClient.ParseGlobal(
            "{\"default_sets\":[3],\"sets\":{\"3\":{\"emoticons\":[" +
            "{\"id\":1,\"name\":\"ZreknarF\",\"urls\":{\"1\":\"https://cdn.frankerfacez.com/emote/1/1\",\"2\":\"https://cdn.frankerfacez.com/emote/1/2\"}}," +
            "{\"id\":2,\"name\":\"OnlyOne\",\"urls\":{\"1\":\"//cdn.frankerfacez.com/emote/2/1\"}}," +
            "{\"id\":3,\"name\":\"Hidden\",\"hidden\":true,\"urls\":{\"1\":\"https://x/3\"}}]}," +
            "\"99\":{\"emoticons\":[{\"id\":4,\"name\":\"NotDefault\",\"urls\":{\"1\":\"https://x/4\"}}]}}}");

        Assert.Equal(new[]
        {
            new ExtraEmote("ZreknarF", "https://cdn.frankerfacez.com/emote/1/2", false, ExtraSource.Ffz),
            new ExtraEmote("OnlyOne", "https://cdn.frankerfacez.com/emote/2/1", false, ExtraSource.Ffz),
        }, emotes);
    }

    [Fact]
    public void Ffz_channel_emotes_come_from_the_rooms_sets()
    {
        var emotes = FfzClient.ParseRoom("{\"room\":{\"set\":500},\"sets\":{\"500\":{\"emoticons\":[{\"id\":9,\"name\":\"roomEmote\",\"urls\":{\"2\":\"https://x/9/2\"}}]}}}");

        Assert.Equal("roomEmote", Assert.Single(emotes).Name);
    }

    private sealed class Source : IExtraEmoteApi
    {
        private readonly ExtraEmote[] _global;
        private readonly ExtraEmote[] _channel;
        public bool Fail;

        public Source(ExtraEmote[] global, ExtraEmote[]? channel = null)
        {
            _global = global;
            _channel = channel ?? Array.Empty<ExtraEmote>();
        }

        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) =>
            Fail ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<ExtraEmote>>(_global);

        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) =>
            Fail ? throw new HttpRequestException("down") : Task.FromResult<IReadOnlyList<ExtraEmote>>(_channel);
    }

    private static ExtraEmote E(string name, ExtraSource source, string url = "") => new(name, url.Length > 0 ? url : "http://" + source + "/" + name, false, source);

    [Fact]
    public async Task One_service_being_down_does_not_take_the_others_with_it()
    {
        var seven = new Source(new[] { E("a", ExtraSource.SevenTv) });
        var bttv = new Source(new[] { E("b", ExtraSource.Bttv) }) { Fail = true };
        var sources = new ExtraEmoteSources(("7TV", seven), ("BTTV", bttv));

        Assert.Equal(new[] { "a" }, (await sources.GetGlobalAsync(CancellationToken.None)).Select(e => e.Name).ToArray());
        seven.Fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => sources.GetGlobalAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_channels_emotes_win_over_global_ones_and_7tv_wins_over_bttv_and_ffz()
    {
        var seven = new Source(new[] { E("same", ExtraSource.SevenTv), E("global", ExtraSource.SevenTv) });
        var bttv = new Source(new[] { E("same", ExtraSource.Bttv), E("bttvOnly", ExtraSource.Bttv) }, new[] { E("global", ExtraSource.Bttv) });
        var ffz = new Source(new[] { E("same", ExtraSource.Ffz), E("ffzOnly", ExtraSource.Ffz) });
        var catalog = new ExtraEmoteCatalog(new ExtraEmoteSources(("7TV", seven), ("BTTV", bttv), ("FFZ", ffz)), () => "777");

        await catalog.EnsureLoadedAsync();

        Assert.True(catalog.TryGet("same", out var same));
        Assert.Equal(ExtraSource.SevenTv, same.Source);
        Assert.True(catalog.TryGet("global", out var global));
        Assert.Equal(ExtraSource.Bttv, global.Source);
        Assert.True(catalog.TryGet("bttvOnly", out _));
        Assert.True(catalog.TryGet("ffzOnly", out _));
    }

    [Fact]
    public void Bttv_and_ffz_global_emotes_get_their_own_shelves_and_channel_ones_go_to_the_channel()
    {
        var all = EmoteChoices.Collect(null, Array.Empty<TwitchEmote>(), Array.Empty<TwitchEmote>(),
            new[] { E("chanBttv", ExtraSource.Bttv) },
            new[] { E("sevenG", ExtraSource.SevenTv), E("bttvG", ExtraSource.Bttv), E("ffzG", ExtraSource.Ffz) },
            "777", e => e.Id);

        var shelves = EmoteChoices.Shelves(all, null, "");

        Assert.Equal(new[] { "channel", "7tv", "bttv", "ffz" }, shelves.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { "Канал", "7TV", "BTTV", "FFZ" }, shelves.Select(s => s.Title).ToArray());
        Assert.Equal("chanBttv", Assert.Single(shelves[0].Emotes).Name);
        Assert.Equal("ffzG", Assert.Single(shelves[3].Emotes).Name);
    }
}
