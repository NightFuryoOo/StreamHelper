using System.Net;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RedemptionVideoTests
{
    private sealed class Clips : IClipApi
    {
        public LinkPreview? Answer { get; set; }

        public Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct) => Task.FromResult(Answer);
    }

    private static LinkPreviewCatalog Catalog(FakeWeb web, Clips? clips = null) =>
        new(clips ?? new Clips(), new YouTubeClient(new HttpClient(web), "http://yt.test", "http://img.test"), new ImageCache(new HttpClient(web)));

    [Theory]
    [InlineData("https://www.youtube.com/live/yrrnLY1kIQg?si=lHlfA8pCkkveDiZO", PreviewKind.YouTube, "yrrnLY1kIQg")]
    [InlineData("Разбери вот это https://youtu.be/dQw4w9WgXcQ пожалуйста", PreviewKind.YouTube, "dQw4w9WgXcQ")]
    [InlineData("клип https://clips.twitch.tv/FunnySlug", PreviewKind.TwitchClip, "FunnySlug")]
    public void The_first_video_or_clip_link_in_the_viewer_text_is_found(string text, PreviewKind kind, string id)
    {
        var target = LinkPreviewCatalog.TargetIn(text);

        Assert.Equal((kind, id), (target!.Kind, target.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("просто текст")]
    [InlineData("https://steamcommunity.com/id/NightFuryo3o")]
    public void Text_without_a_video_link_gets_no_preview(string text)
    {
        Assert.Null(LinkPreviewCatalog.TargetIn(text));
    }

    [Fact]
    public async Task A_youtube_link_shows_its_picture_at_once_and_its_title_when_youtube_answers()
    {
        var web = new FakeWeb { Answer = _ => (HttpStatusCode.OK, "{\"title\":\"Разбор игры\",\"author_name\":\"Канал\"}") };
        var redemption = new Redemption { UserInput = "https://www.youtube.com/live/yrrnLY1kIQg?si=x" };

        var attaching = Catalog(web).AttachAsync(redemption);
        Assert.True(redemption.HasVideo);
        Assert.False(redemption.HasVideoTitle);
        Assert.Equal(YouTubeImages.Thumbnail("yrrnLY1kIQg"), redemption.VideoThumbnail);
        await attaching;

        Assert.Equal("Разбор игры", redemption.VideoTitle);
        Assert.Equal("https://www.youtube.com/live/yrrnLY1kIQg?si=x", redemption.VideoUrl);
        Assert.Equal("http://img.test/vi/yrrnLY1kIQg/mqdefault.jpg", redemption.VideoThumbnail);
        Assert.Equal(("Видео YouTube", "Видео: Разбор игры"), (redemption.VideoLabel, redemption.VideoName));
        Assert.True(redemption.HasVideoTitle);
    }

    [Fact]
    public async Task A_video_youtube_does_not_know_keeps_its_picture_and_shows_the_link_as_its_title()
    {
        var web = new FakeWeb { Answer = _ => (HttpStatusCode.NotFound, "") };
        var redemption = new Redemption { UserInput = "https://youtu.be/dQw4w9WgXcQ" };

        await Catalog(web).AttachAsync(redemption);

        Assert.True(redemption.HasVideo);
        Assert.Equal("https://youtu.be/dQw4w9WgXcQ", redemption.VideoTitle);
        Assert.False(redemption.HasVideoTitle);
    }

    [Fact]
    public async Task A_twitch_clip_appears_only_once_twitch_gives_its_picture_and_title()
    {
        var clips = new Clips { Answer = new LinkPreview(PreviewKind.TwitchClip, "u", "Коротко об ивенте", "Канал", "Автор", 243, null, "http://cdn.test/clip.jpg") };
        var redemption = new Redemption { UserInput = "https://clips.twitch.tv/FunnySlug" };

        await Catalog(new FakeWeb(), clips).AttachAsync(redemption);

        Assert.True(redemption.VideoIsClip);
        Assert.Equal(("Коротко об ивенте", "http://cdn.test/clip.jpg", "Клип Twitch"), (redemption.VideoTitle, redemption.VideoThumbnail, redemption.VideoLabel));

        var missing = new Redemption { UserInput = "https://clips.twitch.tv/GoneSlug" };
        clips.Answer = null;
        await Catalog(new FakeWeb(), clips).AttachAsync(missing);
        Assert.False(missing.HasVideo);
    }

    [Fact]
    public async Task A_titled_video_is_kept_after_a_restart_and_is_not_asked_for_again()
    {
        using var dir = new TempDir();
        var web = new FakeWeb { Answer = _ => (HttpStatusCode.OK, "{\"title\":\"Разбор игры\"}") };
        var store = new RedemptionStore(dir.File("redemptions.json"));
        var redemption = new Redemption { Key = "r1", UserInput = "https://youtu.be/dQw4w9WgXcQ" };
        store.AddRange(new[] { redemption });
        await Catalog(web).AttachAsync(redemption);

        var loaded = Assert.Single(new RedemptionStore(dir.File("redemptions.json")).Items);
        Assert.Equal(("Разбор игры", "https://youtu.be/dQw4w9WgXcQ"), (loaded.VideoTitle, loaded.VideoUrl));
        Assert.True(loaded.HasVideoTitle);

        var asked = web.Asked.Count;
        await Catalog(web).AttachAsync(loaded);
        Assert.Equal(asked, web.Asked.Count);
    }

    [Fact]
    public void A_donation_video_keeps_its_own_label()
    {
        var donation = new Donation();

        Assert.Equal(("Заказанное видео", false), (donation.VideoLabel, donation.VideoIsClip));
    }
}
