using System.Windows.Documents;
using StreamHelper.Api;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatLinksTests
{
    private static string Show(string text) =>
        string.Join("|", ChatLinks.Split(text).Select(p => p.Link == null ? "'" + p.Text + "'" : "[" + p.Text + " -> " + p.Link.AbsoluteUri + "]"));

    [Fact]
    public void A_twitch_clip_link_becomes_one_link()
    {
        Assert.Equal("[https://www.twitch.tv/divaricci/clip/EnthusiasticPhilanthropicOryxLitFam-vYmgLeY6Ft33LqBO -> https://www.twitch.tv/divaricci/clip/EnthusiasticPhilanthropicOryxLitFam-vYmgLeY6Ft33LqBO]",
            Show("https://www.twitch.tv/divaricci/clip/EnthusiasticPhilanthropicOryxLitFam-vYmgLeY6Ft33LqBO"));
    }

    [Fact]
    public void Text_around_links_stays_text_and_www_links_open_over_https()
    {
        Assert.Equal("'смотри '|[www.youtube.com/watch?v=abc -> https://www.youtube.com/watch?v=abc]|' и '|[http://example.com -> http://example.com/]|' тоже'",
            Show("смотри www.youtube.com/watch?v=abc и http://example.com тоже"));
    }

    [Theory]
    [InlineData("глянь https://example.com/page.", "https://example.com/page")]
    [InlineData("ссылка: https://example.com/a?b=1!", "https://example.com/a?b=1")]
    [InlineData("(https://example.com/x)", "https://example.com/x")]
    [InlineData("https://en.wikipedia.org/wiki/Mercury_(planet)", "https://en.wikipedia.org/wiki/Mercury_(planet)")]
    [InlineData("«https://example.com/q»", "https://example.com/q")]
    public void Punctuation_at_the_end_is_not_part_of_the_link(string text, string expected)
    {
        var link = Assert.Single(ChatLinks.Split(text), p => p.Link != null);
        Assert.Equal(expected, link.Text);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("http://localhost/admin")]
    [InlineData("https://")]
    [InlineData("слово www. ещё")]
    [InlineData("просто текст без ссылок")]
    public void Only_real_web_addresses_become_links(string text)
    {
        Assert.All(ChatLinks.Split(text), p => Assert.Null(p.Link));
        Assert.Equal(text, string.Concat(ChatLinks.Split(text).Select(p => p.Text)));
    }

    [Fact]
    public void Several_links_in_one_message_are_all_found()
    {
        var parts = ChatLinks.Split("a https://one.com b https://two.org/x c");

        Assert.Equal(new[] { "https://one.com/", "https://two.org/x" }, parts.Where(p => p.Link != null).Select(p => p.Link!.AbsoluteUri).ToArray());
        Assert.Equal("a https://one.com b https://two.org/x c", string.Concat(parts.Select(p => p.Text)));
    }

    [Fact]
    public void In_the_chat_a_link_is_a_hyperlink_and_the_rest_stays_plain_text()
    {
        Sta.Run(() =>
        {
            var catalog = new ExtraEmoteCatalog(new NoEmotes(), () => "");
            var emotes = new ChatEmotes(catalog, new ImageCache(new HttpClient()), TwitchEndpoints.Default);
            var message = new ChatMessage("m", "42", "v", "V", "клип https://clips.twitch.tv/Abc тут", Array.Empty<ChatMention>(), DateTime.UtcNow);

            var inlines = emotes.Inlines(message, 14);

            var link = Assert.Single(inlines.OfType<Hyperlink>());
            Assert.Equal("https://clips.twitch.tv/Abc", link.NavigateUri.AbsoluteUri);
            Assert.Equal("https://clips.twitch.tv/Abc", new TextRange(link.ContentStart, link.ContentEnd).Text);
            Assert.Equal(new[] { "клип ", " тут" }, inlines.OfType<Run>().Select(r => r.Text).ToArray());
        });
    }

    private sealed class NoEmotes : IExtraEmoteApi
    {
        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());

        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(Array.Empty<ExtraEmote>());
    }
}
