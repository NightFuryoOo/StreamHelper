using System.IO;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatGifTests
{
    private const string Title = "[Dance Cat GIF by Respective]";
    private const string Url = "https://media4.giphy.com/media/joSNxeswxuc74Juo8X/giphy.gif";

    private static ChatMessage Parse(string fragment) =>
        EventSubParser.Parse(MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType,
            "{\"broadcaster_user_id\":\"777\",\"chatter_user_id\":\"42\",\"chatter_user_login\":\"vsego\",\"chatter_user_name\":\"Всего_Хорошего\"," +
            "\"message_id\":\"g-1\",\"message\":{\"text\":\"" + Title + "\",\"fragments\":[" + fragment + "]},\"color\":\"\",\"badges\":[]}"))!.Chat!;

    private static ChatEmotes Emotes(bool gifsOn = true, FakeWeb? web = null)
    {
        var catalog = new ExtraEmoteCatalog(new NoExtras(), () => "777");
        var http = new HttpClient(web ?? new FakeWeb());
        return new ChatEmotes(catalog, new ImageCache(http), TwitchEndpoints.Default, gifs: ChatEmotes.GifCache(http), gifsOn: () => gifsOn);
    }

    private sealed class NoExtras : IExtraEmoteApi
    {
        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());

        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());
    }

    private static ChatMessage GifMessage() =>
        new("g-1", "42", "vsego", "Всего_Хорошего", Title, Array.Empty<ChatMention>(), DateTime.UtcNow, Fragments: new[] { new ChatFragment("gif", Title, Url: Url) });

    [Fact]
    public void A_gif_from_the_gif_keyboard_keeps_its_title_and_link()
    {
        var documented = Parse("{\"type\":\"gif\",\"text\":\"" + Title + "\",\"gif\":{\"id\":\"joSNxeswxuc74Juo8X\",\"url\":\"" + Url + "\"}}");
        var seen = Parse("{\"type\":\"gif\",\"text\":\"" + Title + "\",\"gif\":{\"gif_id\":\"joSNxeswxuc74Juo8X\",\"url\":\"" + Url + "\"}}");

        Assert.Equal(new ChatFragment("gif", Title, Url: Url), Assert.Single(documented.Fragments!));
        Assert.Equal(new ChatFragment("gif", Title, Url: Url), Assert.Single(seen.Fragments!));
        Assert.Equal(Title, documented.Text);
        Assert.True(ChatEmotes.HasGif(documented));
    }

    [Fact]
    public void A_gif_without_a_safe_link_stays_its_title()
    {
        var plain = Parse("{\"type\":\"gif\",\"text\":\"" + Title + "\",\"gif\":{\"id\":\"x\",\"url\":\"http://media4.giphy.com/x.gif\"}}");
        var empty = Parse("{\"type\":\"gif\",\"text\":\"" + Title + "\"}");

        Assert.Equal(new ChatFragment("text", Title), Assert.Single(plain.Fragments!));
        Assert.Equal(new ChatFragment("text", Title), Assert.Single(empty.Fragments!));
        Assert.False(ChatEmotes.HasGif(plain));
    }

    [Fact]
    public void The_switch_turns_the_gif_into_its_title()
    {
        Assert.Equal(new[] { (Title, (string?)Url, true) }, Emotes().Pieces(GifMessage()).Select(p => (p.Text, p.Url, p.Gif)).ToArray());
        Assert.Equal(new[] { (Title, (string?)null, false) }, Emotes(gifsOn: false).Pieces(GifMessage()).Select(p => (p.Text, p.Url, p.Gif)).ToArray());
        Assert.True(new AppSettings().ChatGifs);
        Assert.Contains(nameof(AppSettings.ChatGifs), Profile.SharedSettings);
    }

    [Fact]
    public void A_gif_is_a_hundred_points_tall_and_counts_as_a_picture()
    {
        Assert.Equal(100, ChatEmotes.HeightFor(15, ChatEmotes.GifTag));
        Assert.Equal(100, ChatEmotes.HeightFor(24, ChatEmotes.GifTag));
        Assert.True(ChatEmotes.IsEmote(ChatEmotes.GifTag));
    }

    private static byte[] Frames(int count, int width, int height) =>
        TinyGif.Build(width, height, Enumerable.Range(0, count)
            .Select(i => new TinyGif.Frame(0, 0, width, height, Enumerable.Repeat((byte)(i % 3), width * height).ToArray(), DelayCs: 10)).ToArray());

    [Fact]
    public void A_big_gif_is_made_smaller_while_it_is_read_and_keeps_its_speed()
    {
        var gif = Frames(6, 40, 30);

        var full = EmoteDecoder.Decode(gif)!;
        var small = EmoteDecoder.Decode(gif, maxHeight: 15)!;

        Assert.Equal((40, 30, 6), (full.Frames[0].PixelWidth, full.Frames[0].PixelHeight, full.Frames.Count));
        Assert.Equal((20, 15, 6), (small.Frames[0].PixelWidth, small.Frames[0].PixelHeight, small.Frames.Count));
        Assert.Equal(full.TotalMs, small.TotalMs);
        Assert.Equal(TinyGif.Pixel(full.Frames[1], 5, 5), TinyGif.Pixel(small.Frames[1], 3, 3));
        Assert.True(small.Frames.All(f => f.IsFrozen));
    }

    [Fact]
    public void Too_many_frames_for_the_memory_limit_are_skipped_and_their_time_goes_to_the_kept_ones()
    {
        var gif = Frames(6, 40, 30);

        var thin = EmoteDecoder.Decode(gif, maxHeight: 15, maxPixelBytes: 2 * 20 * 15 * 4)!;

        Assert.Equal(2, thin.Frames.Count);
        Assert.Equal(new[] { 300, 300 }, thin.DelaysMs.ToArray());
        Assert.Equal(600, thin.TotalMs);
    }

    [Fact]
    public void A_still_picture_is_made_smaller_too()
    {
        var bitmap = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)200, 40 * 30 * 4).ToArray(), 40 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var still = EmoteDecoder.Decode(stream.ToArray(), maxHeight: 15)!;

        Assert.Equal((20, 15, 1), (still.Frames[0].PixelWidth, still.Frames[0].PixelHeight, still.Frames.Count));
    }

    [Fact]
    public async Task The_gif_stands_on_its_own_line_under_the_nick_and_copies_as_its_title()
    {
        var emotes = Emotes();
        var message = GifMessage();
        Assert.False(emotes.IsComplete(message));
        await emotes.CompleteAsync(message).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(emotes.IsComplete(message));

        Sta.Run(() =>
        {
            var inlines = emotes.Inlines(message, 15);
            Assert.Equal(2, inlines.Count);
            Assert.True(ChatEmotes.IsGifBreak(inlines[0]));
            var image = (Image)((InlineUIContainer)inlines[1]).Child;
            Assert.Equal((100.0, 320.0, (object)ChatEmotes.GifTag, (object)Title), (image.Height, image.MaxWidth, image.Tag, image.ToolTip));

            var text = new TextBlock();
            text.Inlines.Add(new Run("Всего_Хорошего"));
            text.Inlines.Add(new Run(": "));
            foreach (var inline in inlines) text.Inlines.Add(inline);
            text.Inlines.Add(new LineBreak());
            text.Inlines.Add(new Run("дальше"));
            Assert.Equal("Всего_Хорошего: " + Title + "\nдальше", ChatSelection.TextBetween(text.ContentStart, text.ContentEnd));
        });
    }

    [Fact]
    public async Task Text_after_a_gif_goes_on_the_next_line_and_a_switched_off_gif_is_plain_text()
    {
        var emotes = Emotes();
        var message = GifMessage() with { Fragments = new[] { new ChatFragment("gif", Title, Url: Url), new ChatFragment("text", " ура") } };
        await emotes.CompleteAsync(message).WaitAsync(TimeSpan.FromSeconds(5));

        Sta.Run(() =>
        {
            var inlines = emotes.Inlines(message, 15);
            Assert.Equal(new[] { "gif-break", "picture", "gif-break", " ура" },
                inlines.Select(i => ChatEmotes.IsGifBreak(i) ? "gif-break" : i is InlineUIContainer ? "picture" : ((Run)i).Text).ToArray());

            var off = Emotes(gifsOn: false).Inlines(GifMessage(), 15);
            Assert.Equal(Title, ((Run)Assert.Single(off)).Text);
        });
    }

    [Fact]
    public async Task Gifs_use_their_own_small_cache_and_not_the_emote_one()
    {
        var web = new FakeWeb();
        var http = new HttpClient(web);
        var images = new ImageCache(http);
        var gifs = ChatEmotes.GifCache(http);
        var emotes = new ChatEmotes(new ExtraEmoteCatalog(new NoExtras(), () => "777"), images, TwitchEndpoints.Default, gifs: gifs);

        await emotes.CompleteAsync(GifMessage()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(gifs.Get(Url));
        Assert.Null(images.Get(Url));
        for (var i = 0; i < 20; i++) await gifs.LoadAsync(Url + "?n=" + i);
        Assert.True(gifs.Count <= 8, gifs.Count.ToString());
    }
}
