using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Api;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

internal static class TinyGif
{
    public sealed record Frame(int Left, int Top, int Width, int Height, byte[] Pixels, int Disposal = 1, int DelayCs = 5, int Transparent = -1);

    public static byte[] Build(int width, int height, params Frame[] frames)
    {
        var o = new List<byte>();
        o.AddRange("GIF89a"u8.ToArray());
        o.AddRange(new byte[] { (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x91, 0, 0 });
        o.AddRange(new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30 });
        foreach (var f in frames)
        {
            var packed = (byte)((f.Disposal << 2) | (f.Transparent >= 0 ? 1 : 0));
            o.AddRange(new byte[] { 0x21, 0xF9, 4, packed, (byte)f.DelayCs, (byte)(f.DelayCs >> 8), (byte)Math.Max(f.Transparent, 0), 0 });
            o.AddRange(new byte[] { 0x2C, (byte)f.Left, (byte)(f.Left >> 8), (byte)f.Top, (byte)(f.Top >> 8), (byte)f.Width, (byte)(f.Width >> 8), (byte)f.Height, (byte)(f.Height >> 8), 0 });
            o.Add(2);
            var data = Lzw(f.Pixels);
            for (var i = 0; i < data.Count; i += 255)
            {
                var n = Math.Min(255, data.Count - i);
                o.Add((byte)n);
                o.AddRange(data.GetRange(i, n));
            }
            o.Add(0);
        }
        o.Add(0x3B);
        return o.ToArray();
    }

    private static List<byte> Lzw(byte[] pixels)
    {
        var bits = new List<bool>();
        void Put(int code) { for (var b = 0; b < 3; b++) bits.Add(((code >> b) & 1) == 1); }
        for (var i = 0; i < pixels.Length; i += 2)
        {
            Put(4);
            Put(pixels[i]);
            if (i + 1 < pixels.Length) Put(pixels[i + 1]);
        }
        Put(5);
        var bytes = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            var value = 0;
            for (var b = 0; b < 8 && i + b < bits.Count; b++) if (bits[i + b]) value |= 1 << b;
            bytes.Add((byte)value);
        }
        return bytes;
    }

    public static string Pixel(BitmapSource frame, int x, int y)
    {
        var px = new byte[4];
        frame.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return $"{px[3]:X2}{px[2]:X2}{px[1]:X2}{px[0]:X2}";
    }
}

public class EmoteDecoderTests
{
    private const string Red = "FFFF0000", Green = "FF00FF00", Blue = "FF0000FF", Clear = "00000000";

    [Fact]
    public void A_gif_comes_out_as_whole_frames_with_their_lengths_and_each_piece_drawn_over_the_one_before()
    {
        var gif = TinyGif.Build(2, 1,
            new TinyGif.Frame(0, 0, 2, 1, new byte[] { 0, 1 }, DelayCs: 5),
            new TinyGif.Frame(1, 0, 1, 1, new byte[] { 2 }, DelayCs: 7));

        var emote = EmoteDecoder.Decode(gif)!;

        Assert.True(emote.IsAnimated);
        Assert.Equal(2, emote.Frames.Count);
        Assert.Equal(new[] { 50, 70 }, emote.DelaysMs.ToArray());
        Assert.Equal(120, emote.TotalMs);
        Assert.Equal((2, 1), (emote.Frames[1].PixelWidth, emote.Frames[1].PixelHeight));
        Assert.Equal((Red, Green), (TinyGif.Pixel(emote.Frames[0], 0, 0), TinyGif.Pixel(emote.Frames[0], 1, 0)));
        Assert.Equal((Red, Blue), (TinyGif.Pixel(emote.Frames[1], 0, 0), TinyGif.Pixel(emote.Frames[1], 1, 0)));
    }

    [Fact]
    public void A_frame_that_asks_to_be_cleared_leaves_a_hole_for_the_next_one()
    {
        var gif = TinyGif.Build(2, 1,
            new TinyGif.Frame(0, 0, 2, 1, new byte[] { 0, 1 }, Disposal: 2),
            new TinyGif.Frame(1, 0, 1, 1, new byte[] { 2 }));

        var emote = EmoteDecoder.Decode(gif)!;

        Assert.Equal((Clear, Blue), (TinyGif.Pixel(emote.Frames[1], 0, 0), TinyGif.Pixel(emote.Frames[1], 1, 0)));
    }

    [Fact]
    public void A_frame_that_asks_for_the_canvas_back_gives_it_back_and_transparent_pixels_show_what_was_below()
    {
        var gif = TinyGif.Build(2, 1,
            new TinyGif.Frame(0, 0, 2, 1, new byte[] { 0, 1 }),
            new TinyGif.Frame(0, 0, 2, 1, new byte[] { 2, 3 }, Disposal: 3, Transparent: 3),
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 3 }, Transparent: 3));

        var emote = EmoteDecoder.Decode(gif)!;

        Assert.Equal((Blue, Green), (TinyGif.Pixel(emote.Frames[1], 0, 0), TinyGif.Pixel(emote.Frames[1], 1, 0)));
        Assert.Equal((Red, Green), (TinyGif.Pixel(emote.Frames[2], 0, 0), TinyGif.Pixel(emote.Frames[2], 1, 0)));
    }

    [Fact]
    public void A_missing_or_tiny_delay_becomes_a_tenth_of_a_second_like_in_a_browser()
    {
        var gif = TinyGif.Build(1, 1,
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 0 }, DelayCs: 0),
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 1 }, DelayCs: 1),
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 2 }, DelayCs: 2));

        var emote = EmoteDecoder.Decode(gif)!;

        Assert.Equal(new[] { 100, 100, 20 }, emote.DelaysMs.ToArray());
    }

    [Fact]
    public void A_still_picture_is_one_frame_that_does_not_move()
    {
        var pixels = new byte[] { 0, 0, 255, 255 };
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);

        var emote = EmoteDecoder.Decode(stream.ToArray())!;

        Assert.False(emote.IsAnimated);
        Assert.Single(emote.Frames);
        Assert.Equal(0, emote.FrameAt(123456));
        Assert.Equal(Red, TinyGif.Pixel(emote.Frames[0], 0, 0));
    }

    [Fact]
    public void Bytes_that_are_not_a_picture_give_nothing_instead_of_an_error()
    {
        Assert.Null(EmoteDecoder.Decode(Array.Empty<byte>()));
        Assert.Null(EmoteDecoder.Decode(new byte[] { 1, 2, 3 }));
        Assert.Null(EmoteDecoder.Decode("<html>not found</html>"u8.ToArray()));
        Assert.Null(EmoteDecoder.Decode("GIF89a-broken-broken-broken"u8.ToArray()));
    }

    [Fact]
    public void The_frame_to_show_goes_round_and_round_by_the_clock()
    {
        var gif = TinyGif.Build(1, 1,
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 0 }, DelayCs: 10),
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 1 }, DelayCs: 20),
            new TinyGif.Frame(0, 0, 1, 1, new byte[] { 2 }, DelayCs: 30));
        var emote = EmoteDecoder.Decode(gif)!;

        Assert.Equal(600, emote.TotalMs);
        Assert.Equal(new[] { 0, 0, 1, 1, 2, 2, 0 }, new[] { 0L, 99, 100, 299, 300, 599, 600 }.Select(emote.FrameAt).ToArray());
        Assert.Equal(1, emote.FrameAt(600 * 1000 + 150));
    }
}

public class SevenTvTests
{
    private const string Set =
        "{\"emotes\":[" +
        "{\"id\":\"a\",\"name\":\"furaNEGNORb\",\"data\":{\"animated\":true,\"host\":{\"url\":\"//cdn.7tv.app/emote/AAA\",\"files\":[{\"name\":\"1x.webp\"},{\"name\":\"1x.gif\"},{\"name\":\"2x.webp\"},{\"name\":\"2x.gif\"},{\"name\":\"4x.gif\"}]}}}," +
        "{\"id\":\"b\",\"name\":\"Still\",\"data\":{\"animated\":false,\"host\":{\"url\":\"//cdn.7tv.app/emote/BBB/\",\"files\":[{\"name\":\"1x.png\"},{\"name\":\"2x.png\"},{\"name\":\"2x.webp\"}]}}}," +
        "{\"id\":\"c\",\"name\":\"OnlyOne\",\"data\":{\"animated\":true,\"host\":{\"url\":\"http://127.0.0.1:9/CCC\",\"files\":[{\"name\":\"4x.gif\"}]}}}," +
        "{\"id\":\"d\",\"name\":\"WebpOnly\",\"data\":{\"animated\":false,\"host\":{\"url\":\"//cdn.7tv.app/emote/DDD\",\"files\":[{\"name\":\"2x.webp\"}]}}}," +
        "{\"id\":\"e\",\"data\":{\"name\":\"Aliased\",\"animated\":false,\"host\":{\"url\":\"//cdn.7tv.app/emote/EEE\",\"files\":[{\"name\":\"2x.png\"}]}}}," +
        "{\"id\":\"f\",\"name\":\"NoHost\",\"data\":{\"animated\":false}}," +
        "{\"id\":\"g\",\"name\":\"\",\"data\":{\"animated\":false,\"host\":{\"url\":\"//x\",\"files\":[{\"name\":\"2x.png\"}]}}}]}";

    [Fact]
    public void A_moving_emote_gets_its_double_size_gif_and_a_still_one_its_png()
    {
        var emotes = SevenTvClient.ParseSet(Set, "emotes").ToDictionary(e => e.Name);

        Assert.Equal(new SevenTvEmote("furaNEGNORb", "https://cdn.7tv.app/emote/AAA/2x.gif", true), emotes["furaNEGNORb"]);
        Assert.Equal(new SevenTvEmote("Still", "https://cdn.7tv.app/emote/BBB/2x.png", false), emotes["Still"]);
    }

    [Fact]
    public void Another_size_is_taken_when_there_is_no_double_one_and_a_full_address_is_kept_as_it_is()
    {
        var emotes = SevenTvClient.ParseSet(Set, "emotes").ToDictionary(e => e.Name);

        Assert.Equal("http://127.0.0.1:9/CCC/4x.gif", emotes["OnlyOne"].Url);
    }

    [Fact]
    public void An_emote_that_cannot_be_drawn_is_left_out_and_the_name_may_come_from_the_data()
    {
        var names = SevenTvClient.ParseSet(Set, "emotes").Select(e => e.Name).ToArray();

        Assert.Contains("Aliased", names);
        Assert.DoesNotContain("WebpOnly", names);
        Assert.DoesNotContain("NoHost", names);
        Assert.Equal(4, names.Length);
    }

    [Fact]
    public void The_channel_answer_keeps_its_emotes_one_level_down_and_nonsense_gives_nothing()
    {
        var channel = "{\"id\":\"1\",\"emote_set\":" + Set + "}";

        Assert.Equal(4, SevenTvClient.ParseSet(channel, "emote_set", "emotes").Count);
        Assert.Empty(SevenTvClient.ParseSet("{\"emote_set\":null}", "emote_set", "emotes"));
        Assert.Empty(SevenTvClient.ParseSet("{}", "emote_set", "emotes"));
        Assert.Empty(SevenTvClient.ParseSet("not json", "emotes"));
        Assert.Empty(SevenTvClient.ParseSet("[]", "emotes"));
    }

    private sealed class FakeApi : ISevenTvApi
    {
        public List<SevenTvEmote> Global = new();
        public List<SevenTvEmote> Channel = new();
        public bool FailGlobal, FailChannel;
        public int Calls;
        public string? AskedChannel;

        public Task<IReadOnlyList<SevenTvEmote>> GetGlobalAsync(CancellationToken ct)
        {
            Calls++;
            if (FailGlobal) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyList<SevenTvEmote>>(Global);
        }

        public Task<IReadOnlyList<SevenTvEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
        {
            AskedChannel = twitchUserId;
            if (FailChannel) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyList<SevenTvEmote>>(Channel);
        }
    }

    [Fact]
    public async Task The_channels_own_emote_wins_over_the_global_one_with_the_same_word()
    {
        var api = new FakeApi
        {
            Global = { new SevenTvEmote("Same", "https://g/global.gif", true), new SevenTvEmote("OnlyGlobal", "https://g/og.png", false) },
            Channel = { new SevenTvEmote("Same", "https://c/channel.gif", true) },
        };
        var catalog = new SevenTvCatalog(api, () => "777");

        await catalog.EnsureLoadedAsync();

        Assert.Equal("777", api.AskedChannel);
        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet("Same", out var same));
        Assert.Equal("https://c/channel.gif", same.Url);
        Assert.True(catalog.TryGet("OnlyGlobal", out _));
        Assert.False(catalog.TryGet("same", out _));
    }

    [Fact]
    public async Task One_half_failing_still_gives_the_other_half_and_the_next_try_waits()
    {
        var api = new FakeApi { Global = { new SevenTvEmote("G", "https://g/g.png", false) }, FailChannel = true };
        var catalog = new SevenTvCatalog(api, () => "777");

        await catalog.EnsureLoadedAsync();
        await catalog.EnsureLoadedAsync();

        Assert.True(catalog.TryGet("G", out _));
        Assert.Equal(1, api.Calls);
    }

    [Fact]
    public async Task Nothing_loaded_and_everything_failing_leaves_an_empty_catalog_without_an_error()
    {
        var api = new FakeApi { FailGlobal = true, FailChannel = true };
        var catalog = new SevenTvCatalog(api, () => "777");

        await catalog.EnsureLoadedAsync();

        Assert.Equal(0, catalog.Count);
        Assert.False(catalog.TryGet("anything", out _));
    }

    [Fact]
    public async Task The_client_asks_the_global_and_the_channel_addresses_and_treats_an_unknown_channel_as_empty()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (req, _) => req.Url!.AbsolutePath switch
        {
            "/v3/emote-sets/global" => (200, Set),
            "/v3/users/twitch/777" => (200, "{\"emote_set\":" + Set + "}"),
            _ => (404, "{}"),
        };
        var client = new SevenTvClient(new System.Net.Http.HttpClient(), server.BaseUrl);

        Assert.Equal(4, (await client.GetGlobalAsync(CancellationToken.None)).Count);
        Assert.Equal(4, (await client.GetChannelAsync("777", CancellationToken.None)).Count);
        Assert.Empty(await client.GetChannelAsync("999", CancellationToken.None));
        Assert.Empty(await client.GetChannelAsync("", CancellationToken.None));
        Assert.Contains("GET /v3/users/twitch/999", server.Requests[2]);
    }
}

public class ChatEmoteMessageTests
{
    private static string Event(string fragments) =>
        "{\"broadcaster_user_id\":\"777\",\"chatter_user_id\":\"42\",\"chatter_user_login\":\"viewer\",\"chatter_user_name\":\"Viewer\"," +
        "\"message_id\":\"cm-1\",\"message\":{\"text\":\"Kappa hi\",\"fragments\":" + fragments + "},\"color\":\"\",\"badges\":[],\"message_type\":\"text\"}";

    [Fact]
    public void The_pieces_of_a_message_come_with_Twitch_emotes_and_whether_they_move()
    {
        var json = MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType, Event(
            "[{\"type\":\"emote\",\"text\":\"Kappa\",\"emote\":{\"id\":\"25\",\"emote_set_id\":\"0\",\"owner_id\":\"0\",\"format\":[\"static\"]}}," +
            "{\"type\":\"text\",\"text\":\" hi \"}," +
            "{\"type\":\"emote\",\"text\":\"catJAM\",\"emote\":{\"id\":\"emotesv2_x\",\"format\":[\"static\",\"animated\"]}}," +
            "{\"type\":\"mention\",\"text\":\"@bob\",\"mention\":{\"user_id\":\"2\",\"user_login\":\"bob\",\"user_name\":\"Bob\"}}," +
            "{\"type\":\"cheermote\",\"text\":\"cheer100\"}]"));

        var chat = EventSubParser.Parse(json)!.Chat!;

        Assert.Equal(
            new[]
            {
                new ChatFragment("emote", "Kappa", "25", false),
                new ChatFragment("text", " hi "),
                new ChatFragment("emote", "catJAM", "emotesv2_x", true),
                new ChatFragment("text", "@bob"),
                new ChatFragment("text", "cheer100"),
            },
            chat.Fragments!.ToArray());
        Assert.Equal(new[] { "bob" }, chat.Mentions.Select(m => m.Login).ToArray());
    }

    [Fact]
    public void A_message_without_fragments_has_none_and_is_drawn_from_its_text()
    {
        var json = MockEventSubServer.Notification("n1", EventSubParser.ChatMessageType, Event("[]"));
        Assert.Null(EventSubParser.Parse(json)!.Chat!.Fragments);
    }

    [Fact]
    public void A_text_cut_into_words_and_spaces_gives_the_same_text_back()
    {
        foreach (var text in new[] { "", "a", "a b", "  a  b  ", "furaNEGNORb furaNEGNORb", " ", "word," })
        {
            Assert.Equal(text, string.Concat(ChatEmotes.Split(text)));
        }
        Assert.Equal(new[] { "a", " ", "b" }, ChatEmotes.Split("a b").ToArray());
        Assert.Equal(new[] { "  ", "a", "  " }, ChatEmotes.Split("  a  ").ToArray());
    }

    [Fact]
    public void Twitch_emote_pictures_are_asked_for_by_id_still_or_moving()
    {
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0", TwitchEndpoints.Default.EmoteUrl("25", false));
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/emotesv2_abc/animated/dark/2.0", TwitchEndpoints.Default.EmoteUrl("emotesv2_abc", true));
        Assert.Equal("http://127.0.0.1:9/emoticons/v2/7/static/dark/2.0", TwitchEndpoints.FromBase("http://127.0.0.1:9").EmoteUrl("7", false));
    }

    [Fact]
    public async Task Words_of_the_catalog_in_a_message_become_pieces_with_an_address_and_the_rest_stays_text()
    {
        var api = new SevenTvTests_Api(new SevenTvEmote("furaNEGNORb", "https://cdn/fura.gif", true));
        var catalog = new SevenTvCatalog(api, () => "777");
        await catalog.EnsureLoadedAsync();
        var emotes = new ChatEmotes(catalog, new EmoteImageCache(new System.Net.Http.HttpClient()), TwitchEndpoints.Default);
        var message = new ChatMessage("m", "42", "v", "V", "hello furaNEGNORb furaNEGNORb bye", Array.Empty<ChatMention>(), DateTime.UtcNow);

        var pieces = emotes.Pieces(message);

        Assert.Equal(
            new[] { ("hello ", (string?)null), ("furaNEGNORb", "https://cdn/fura.gif"), (" ", null), ("furaNEGNORb", "https://cdn/fura.gif"), (" bye", null) },
            pieces.Select(p => (p.Text, p.Url)).ToArray());
        Assert.Equal(message.Text, string.Concat(pieces.Select(p => p.Text)));
    }

    [Fact]
    public async Task Twitch_emotes_in_the_fragments_are_pieces_too_and_a_word_inside_a_text_fragment_is_still_looked_up()
    {
        var api = new SevenTvTests_Api(new SevenTvEmote("Seven", "https://cdn/seven.png", false));
        var catalog = new SevenTvCatalog(api, () => "777");
        await catalog.EnsureLoadedAsync();
        var emotes = new ChatEmotes(catalog, new EmoteImageCache(new System.Net.Http.HttpClient()), TwitchEndpoints.Default);
        var message = new ChatMessage("m", "42", "v", "V", "Kappa Seven", Array.Empty<ChatMention>(), DateTime.UtcNow, Fragments: new[]
        {
            new ChatFragment("emote", "Kappa", "25", false),
            new ChatFragment("text", " Seven"),
        });

        var pieces = emotes.Pieces(message);

        Assert.Equal(
            new[] { ("Kappa", (string?)"https://static-cdn.jtvnw.net/emoticons/v2/25/static/dark/2.0"), (" ", null), ("Seven", "https://cdn/seven.png") },
            pieces.Select(p => (p.Text, p.Url)).ToArray());
    }

    private sealed class SevenTvTests_Api : ISevenTvApi
    {
        private readonly SevenTvEmote[] _emotes;
        public SevenTvTests_Api(params SevenTvEmote[] emotes) => _emotes = emotes;
        public Task<IReadOnlyList<SevenTvEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<SevenTvEmote>>(_emotes);
        public Task<IReadOnlyList<SevenTvEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SevenTvEmote>>(Array.Empty<SevenTvEmote>());
    }
}