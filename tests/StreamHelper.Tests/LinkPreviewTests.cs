using System.Net;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using StreamHelper.Api;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class LinkPreviewTests
{
    private static string Show(string url) => PreviewLinks.Find(new Uri(url)) is { } t ? $"{t.Kind}:{t.Id}" : "none";

    [Theory]
    [InlineData("https://clips.twitch.tv/AwkwardHelplessSalamanderSwiftRage", "TwitchClip:AwkwardHelplessSalamanderSwiftRage")]
    [InlineData("https://www.twitch.tv/divaricci/clip/EnthusiasticPhilanthropicOryxLitFam-vYmgLeY6Ft33LqBO", "TwitchClip:EnthusiasticPhilanthropicOryxLitFam-vYmgLeY6Ft33LqBO")]
    [InlineData("https://m.twitch.tv/somebody/clip/Slug_1-x?filter=clips&range=7d", "TwitchClip:Slug_1-x")]
    [InlineData("https://clips.twitch.tv/embed?clip=EmbedSlug&parent=example.com", "TwitchClip:EmbedSlug")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", "YouTube:dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abcdef", "YouTube:dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw", "YouTube:jNQXAC9IVRw")]
    [InlineData("https://m.youtube.com/watch?feature=share&v=jNQXAC9IVRw", "YouTube:jNQXAC9IVRw")]
    [InlineData("https://music.youtube.com/watch?v=jNQXAC9IVRw", "YouTube:jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw?si=1", "YouTube:jNQXAC9IVRw")]
    public void Clip_and_video_links_of_every_usual_form_are_recognised(string url, string expected)
    {
        Assert.Equal(expected, Show(url));
    }

    [Theory]
    [InlineData("https://www.twitch.tv/divaricci")]
    [InlineData("https://www.twitch.tv/videos/123456789")]
    [InlineData("https://clips.twitch.tv/")]
    [InlineData("https://www.youtube.com/@RickAstleyYT")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    public void Other_links_get_no_preview(string url)
    {
        Assert.Equal("none", Show(url));
    }

    [Fact]
    public void The_first_clip_or_video_in_a_message_is_the_one_shown_and_its_own_address_is_opened()
    {
        var message = new ChatMessage("m", "1", "v", "V", "глянь https://example.com и www.youtube.com/watch?v=dQw4w9WgXcQ&t=42 и https://clips.twitch.tv/Slug",
            Array.Empty<ChatMention>(), DateTime.UtcNow);

        var target = LinkPreviewCatalog.TargetOf(message)!;

        Assert.Equal(PreviewKind.YouTube, target.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42", target.Url);
        Assert.Null(LinkPreviewCatalog.TargetOf(message with { Text = "просто текст https://example.com" }));
    }

    [Fact]
    public void A_Twitch_clip_answer_gives_title_channel_author_views_length_and_picture()
    {
        const string json = """
            {"data":[{"id":"AwkwardHelplessSalamanderSwiftRage","url":"https://clips.twitch.tv/AwkwardHelplessSalamanderSwiftRage",
            "embed_url":"https://clips.twitch.tv/embed?clip=AwkwardHelplessSalamanderSwiftRage","broadcaster_id":"67955580","broadcaster_name":"ChewieMelodies",
            "creator_id":"53834192","creator_name":"BlackNova03","video_id":"205586603","game_id":"488191","language":"en","title":"babymetal",
            "view_count":1234,"created_at":"2017-11-30T22:34:18Z","thumbnail_url":"https://clips-media-assets.twitch.tv/157589949-preview-480x272.jpg",
            "duration":29.9,"vod_offset":null,"is_featured":false}],"pagination":{}}
            """;

        var clip = PreviewLinks.ParseClip(json)!;

        Assert.Equal(new LinkPreview(PreviewKind.TwitchClip, "https://clips.twitch.tv/AwkwardHelplessSalamanderSwiftRage", "babymetal", "ChewieMelodies", "BlackNova03",
            1234, TimeSpan.FromSeconds(30), "https://clips-media-assets.twitch.tv/157589949-preview-480x272.jpg"), clip);
        Assert.Null(PreviewLinks.ParseClip("{\"data\":[],\"pagination\":{}}"));
    }

    [Fact]
    public void A_YouTube_answer_gives_title_channel_and_the_wide_picture()
    {
        const string json = """
            {"title":"Me at the zoo","author_name":"jawed","author_url":"https://www.youtube.com/@jawed","type":"video","provider_name":"YouTube",
            "thumbnail_url":"https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg","thumbnail_width":480,"thumbnail_height":360}
            """;

        var video = PreviewLinks.ParseVideo(json, "jNQXAC9IVRw", "https://i.ytimg.com")!;

        Assert.Equal(new LinkPreview(PreviewKind.YouTube, "https://www.youtube.com/watch?v=jNQXAC9IVRw", "Me at the zoo", "jawed", "", null, null,
            "https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg"), video);
    }

    [Fact]
    public async Task YouTube_is_asked_through_oEmbed_and_a_hidden_or_missing_video_gives_nothing()
    {
        var web = new FakeWeb { Answer = _ => (HttpStatusCode.OK, "{\"title\":\"Видео\",\"author_name\":\"Канал\"}") };
        var client = new YouTubeClient(new HttpClient(web), "http://yt.test", "http://img.test");

        var video = await client.GetVideoAsync("dQw4w9WgXcQ", CancellationToken.None);

        Assert.Equal("/oembed?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DdQw4w9WgXcQ&format=json", web.Asked[0]);
        Assert.Equal(("Видео", "Канал", "http://img.test/vi/dQw4w9WgXcQ/mqdefault.jpg"), (video!.Title, video.Channel, video.ThumbnailUrl));
        foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.NotFound })
        {
            web.Answer = _ => (status, "");
            Assert.Null(await client.GetVideoAsync("dQw4w9WgXcQ", CancellationToken.None));
        }
        web.Answer = _ => (HttpStatusCode.InternalServerError, "");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetVideoAsync("dQw4w9WgXcQ", CancellationToken.None));
    }

    private sealed class Clips : IClipApi
    {
        public int Asked { get; private set; }
        public Exception? Failure { get; set; }
        public LinkPreview? Answer { get; set; }

        public Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct)
        {
            Asked++;
            return Failure != null ? Task.FromException<LinkPreview?>(Failure) : Task.FromResult(Answer);
        }
    }

    [Fact]
    public async Task Each_clip_is_asked_once_with_its_picture_and_a_missing_or_failed_one_is_not_asked_again_at_once()
    {
        var clips = new Clips { Answer = new LinkPreview(PreviewKind.TwitchClip, "u", "Клип", "Канал", "Автор", 5, TimeSpan.FromSeconds(12), "http://cdn.test/thumb.jpg") };
        var web = new FakeWeb();
        var catalog = new LinkPreviewCatalog(clips, new YouTubeClient(new HttpClient(web), "http://yt.test"), new ImageCache(new HttpClient(web)));
        var target = new PreviewTarget(PreviewKind.TwitchClip, "Slug", "https://clips.twitch.tv/Slug");

        Assert.False(catalog.IsSettled(target));
        var first = catalog.LoadAsync(target);
        var second = catalog.LoadAsync(target);
        var preview = await first;

        Assert.Same(first, second);
        Assert.Equal(1, clips.Asked);
        Assert.NotNull(catalog.Thumbnail(preview!));
        Assert.Same(preview, catalog.Get(target));
        Assert.True(catalog.IsSettled(target));

        var missing = new PreviewTarget(PreviewKind.TwitchClip, "Gone", "https://clips.twitch.tv/Gone");
        clips.Answer = null;
        Assert.Null(await catalog.LoadAsync(missing));
        Assert.Null(await catalog.LoadAsync(missing));
        Assert.True(catalog.IsSettled(missing));

        var broken = new PreviewTarget(PreviewKind.TwitchClip, "Broken", "https://clips.twitch.tv/Broken");
        clips.Failure = new HttpRequestException("down");
        Assert.Null(await catalog.LoadAsync(broken));
        Assert.Null(await catalog.LoadAsync(broken));
        Assert.Equal(3, clips.Asked);
    }

    [Theory]
    [InlineData(1, "1 просмотр")]
    [InlineData(3, "3 просмотра")]
    [InlineData(5, "5 просмотров")]
    [InlineData(12, "12 просмотров")]
    [InlineData(1234, "1 234 просмотра")]
    public void Views_are_counted_in_russian(int count, string expected)
    {
        Assert.Equal(expected, LinkCardText.Views(count));
    }

    [Fact]
    public void The_card_texts_show_length_channel_views_and_who_made_the_clip()
    {
        var clip = new LinkPreview(PreviewKind.TwitchClip, "u", "Эпичный момент", "Streamer", "Viewer", 1234, TimeSpan.FromSeconds(30), "");
        var video = new LinkPreview(PreviewKind.YouTube, "u", "Ролик", "Канал", "", null, null, "");

        Assert.Equal(new[] { "0:30", "1:05", "1:02:03" }, new[] { 30, 65, 3723 }.Select(s => LinkCardText.Duration(TimeSpan.FromSeconds(s))).ToArray());
        Assert.Equal("Streamer · 1 234 просмотра", LinkCardText.Meta(clip));
        Assert.Equal("Клип от Viewer", LinkCardText.Credit(clip));
        Assert.Equal("Клип: Эпичный момент", LinkCardText.Name(clip));
        Assert.Equal("Канал", LinkCardText.Meta(video));
        Assert.Equal("", LinkCardText.Credit(video));
        Assert.Equal("Видео: Ролик", LinkCardText.Name(video));
        Assert.Equal("YouTube", LinkCardText.Meta(video with { Channel = "" }));
    }

    [Fact]
    public async Task A_card_can_be_built_from_a_picture_that_was_decoded_on_another_thread()
    {
        var jpeg = new System.Windows.Media.Imaging.JpegBitmapEncoder();
        var pixels = new byte[16 * 9 * 4];
        Array.Fill(pixels, (byte)200);
        jpeg.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(System.Windows.Media.Imaging.BitmapSource.Create(16, 9, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 16 * 4)));
        using var stream = new MemoryStream();
        jpeg.Save(stream);
        var frames = await Task.Run(() => EmoteDecoder.Decode(stream.ToArray()));
        Assert.NotNull(frames);

        Sta.Run(() =>
        {
            var video = new LinkPreview(PreviewKind.YouTube, "u", "Ролик", "Канал", "", null, null, "t");
            var card = LinkCard.TryCreate(video, frames, "https://youtu.be/dQw4w9WgXcQ", 14, _ => { });
            Assert.NotNull(card);
            var window = new System.Windows.Window { Content = card!.Root, Width = 500, Height = 200, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Left = -10000 };
            window.Show();
            window.UpdateLayout();
            Assert.True(card.Root.ActualWidth > 100);
            window.Close();
        });
    }

    [Fact]
    public void A_card_shows_the_texts_follows_the_text_size_and_opens_the_posted_link_on_click()
    {
        Sta.Run(() =>
        {
            var opened = new List<string>();
            var clip = new LinkPreview(PreviewKind.TwitchClip, "u", "Эпичный момент", "Streamer", "Viewer", 7, TimeSpan.FromSeconds(30), "");
            var card = new LinkCard(clip, null, "https://clips.twitch.tv/Slug", 14, opened.Add);

            Assert.Equal("Клип: Эпичный момент", System.Windows.Automation.AutomationProperties.GetName(card.Root));
            var texts = new List<string>();
            void Walk(System.Windows.DependencyObject node)
            {
                if (node is TextBlock t && t.Visibility == System.Windows.Visibility.Visible) texts.Add(t.Text);
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node).OfType<System.Windows.DependencyObject>()) Walk(child);
            }
            Walk((System.Windows.DependencyObject)card.Root.Content);
            Assert.Equal(new[] { "0:30", "Эпичный момент", "Streamer · 7 просмотров", "Клип от Viewer" }, texts.ToArray());
            Assert.Equal(420, card.Root.MaxWidth);
            card.Resize(20);
            Assert.Equal(600, card.Root.MaxWidth);

            card.Root.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "https://clips.twitch.tv/Slug" }, opened.ToArray());
        });
    }
}
