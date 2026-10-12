using System.Windows.Controls;
using System.Windows.Documents;
using StreamHelper.Api;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatEmoteLoadingTests
{
    private const string Cdn = "http://cdn.test";

    private sealed class HeldCatalog : IExtraEmoteApi
    {
        public TaskCompletionSource<IReadOnlyList<ExtraEmote>> Global { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => Global.Task;

        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());
    }

    private static ChatMessage Message(string text, params ChatFragment[] fragments) =>
        new("m", "42", "v", "V", text, Array.Empty<ChatMention>(), DateTime.UtcNow, Fragments: fragments);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_Twitch_emote_picture_arrives_while_the_7TV_BTTV_FFZ_list_is_still_loading()
    {
        var api = new HeldCatalog();
        var catalog = new ExtraEmoteCatalog(api, () => "777");
        var emotes = new ChatEmotes(catalog, new ImageCache(new HttpClient(new FakeWeb())), TwitchEndpoints.FromBase(Cdn));
        var message = Message("xahtepBooba seven", new ChatFragment("emote", "xahtepBooba", "emotesv2_b", true), new ChatFragment("text", " seven"));
        _ = catalog.EnsureLoadedAsync();

        var done = emotes.CompleteAsync(message);

        await WaitUntil(() => emotes.Shown(message) == 1);
        Assert.False(done.IsCompleted);
        Assert.False(emotes.IsComplete(message));

        api.Global.SetResult(new[] { new ExtraEmote("seven", Cdn + "/7tv/seven.gif", true) });
        await done.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emotes.IsComplete(message));
        Assert.Equal(2, emotes.Shown(message));
    }

    [Fact]
    public async Task Waiting_before_a_line_is_drawn_stops_at_the_limit_and_the_picture_still_comes_later()
    {
        var pictures = new FakeWeb { Held = _ => true };
        var catalog = new ExtraEmoteCatalog(new HeldCatalog(), () => "777");
        var emotes = new ChatEmotes(catalog, new ImageCache(new HttpClient(pictures)), TwitchEndpoints.FromBase(Cdn));
        var message = Message("tigraBanfire", new ChatFragment("emote", "tigraBanfire", "emotesv2_t", true));

        var started = DateTime.UtcNow;
        await emotes.PreloadAsync(message, TimeSpan.FromMilliseconds(200));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Equal(0, emotes.Shown(message));
        pictures.Release();
        await WaitUntil(() => emotes.Shown(message) == 1);
    }

    [Fact]
    public void A_gigantified_emote_message_is_recognised_and_only_its_last_emote_becomes_big()
    {
        string Event(string kind) =>
            "{\"broadcaster_user_id\":\"777\",\"chatter_user_id\":\"42\",\"chatter_user_login\":\"viewer\",\"chatter_user_name\":\"Viewer\"," +
            "\"message_id\":\"cm-1\",\"message\":{\"text\":\"Kappa hi tigraBanfire\",\"fragments\":[" +
            "{\"type\":\"emote\",\"text\":\"Kappa\",\"emote\":{\"id\":\"25\",\"format\":[\"static\"]}}," +
            "{\"type\":\"text\",\"text\":\" hi \"}," +
            "{\"type\":\"emote\",\"text\":\"tigraBanfire\",\"emote\":{\"id\":\"emotesv2_t\",\"format\":[\"static\",\"animated\"]}}]}," +
            "\"color\":\"\",\"badges\":[],\"message_type\":\"" + kind + "\"}";
        var emotes = new ChatEmotes(new ExtraEmoteCatalog(new HeldCatalog(), () => "777"), new ImageCache(new HttpClient(new FakeWeb())), TwitchEndpoints.Default);

        var giant = EventSubParser.Parse(MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType, Event("power_ups_gigantified_emote")))!.Chat!;
        var plain = EventSubParser.Parse(MockEventSubServer.Notification("n2", EventSubParser.ChatMessageType, Event("text")))!.Chat!;

        Assert.True(giant.GiantEmote);
        Assert.False(plain.GiantEmote);
        Assert.Equal(
            new[]
            {
                ("Kappa", (string?)"https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", false),
                (" hi ", null, false),
                ("tigraBanfire", "https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_t/animated/dark/3.0", true),
            },
            emotes.Pieces(giant).Select(p => (p.Text, p.Url, p.Giant)).ToArray());
        Assert.DoesNotContain(emotes.Pieces(plain), p => p.Giant);
    }

    [Fact]
    public async Task A_giant_emote_is_drawn_four_times_taller_than_a_normal_one()
    {
        var api = new HeldCatalog();
        api.Global.SetResult(Array.Empty<ExtraEmote>());
        var emotes = new ChatEmotes(new ExtraEmoteCatalog(api, () => "777"), new ImageCache(new HttpClient(new FakeWeb())), TwitchEndpoints.FromBase(Cdn));
        var message = Message("Kappa tigraBanfire", new ChatFragment("emote", "Kappa", "25"), new ChatFragment("text", " "), new ChatFragment("emote", "tigraBanfire", "emotesv2_t", true))
            with { GiantEmote = true };
        await emotes.CompleteAsync(message).WaitAsync(TimeSpan.FromSeconds(5));

        Sta.Run(() =>
        {
            var images = emotes.Inlines(message, 14).OfType<InlineUIContainer>().Select(i => (Image)i.Child).ToList();

            Assert.Equal(new object[] { ChatEmotes.ImageTag, ChatEmotes.GiantTag }, images.Select(i => i.Tag).ToArray());
            Assert.Equal(new[] { 25.0, 100.0 }, images.Select(i => i.Height).ToArray());
        });
        Assert.Equal(144, ChatEmotes.HeightFor(20, ChatEmotes.GiantTag));
        Assert.Equal(36, ChatEmotes.HeightFor(20, ChatEmotes.ImageTag));
        Assert.True(ChatEmotes.IsEmote(ChatEmotes.GiantTag));
        Assert.False(ChatEmotes.IsEmote(null));
    }

    [Fact]
    public async Task A_full_picture_cache_drops_the_longest_unused_pictures_and_keeps_the_ones_still_loading()
    {
        var pictures = new FakeWeb { Held = uri => uri.AbsoluteUri.EndsWith("/slow") };
        var cache = new ImageCache(new HttpClient(pictures));
        var slow = cache.LoadAsync(Cdn + "/slow");
        for (var i = 0; i < 399; i++) await cache.LoadAsync(Cdn + "/" + i);
        Assert.NotNull(cache.Get(Cdn + "/0"));

        await cache.LoadAsync(Cdn + "/new");

        Assert.NotNull(cache.Get(Cdn + "/0"));
        Assert.NotNull(cache.Get(Cdn + "/new"));
        Assert.Null(cache.Get(Cdn + "/1"));
        Assert.True(cache.Count <= 302);
        pictures.Release();
        await slow.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(cache.Get(Cdn + "/slow"));
    }
}
