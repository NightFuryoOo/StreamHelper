using System.Net;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class StreamInfoTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat channel:manage:broadcast clips:edit")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Fact]
    public void The_login_asks_for_the_title_and_clip_rights()
    {
        Assert.Contains(" channel:manage:broadcast", TwitchClient.Scopes);
        Assert.Contains(" clips:edit", TwitchClient.Scopes);
        var all = new AppSettings { TwitchScopes = TwitchClient.Scopes };
        Assert.True(all.HasBroadcastScope);
        Assert.True(all.HasClipsScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat" }.HasBroadcastScope);
    }

    [Fact]
    public async Task The_window_reads_the_title_and_category_of_your_own_channel()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (_, _) => (200, """{"data":[{"broadcaster_id":"777","broadcaster_login":"me","broadcaster_name":"Me","broadcaster_language":"ru","game_id":"491487","game_name":"Dead by Daylight","title":"Убийца ночью о3о","delay":0,"tags":["русский"]}]}""");
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var info = await client.GetChannelInfoAsync(CancellationToken.None);

        Assert.Equal(new ChannelInfo("Убийца ночью о3о", "491487", "Dead by Daylight"), info);
        Assert.StartsWith("GET /helix/channels?broadcaster_id=777 ", Assert.Single(twitch.Requests));
    }

    [Fact]
    public async Task Saving_sends_the_trimmed_title_and_the_category()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (_, _) => (204, "");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var result = await client.UpdateChannelInfoAsync("  Новый стрим  ", "491487", CancellationToken.None);
        var cleared = await client.UpdateChannelInfoAsync("Без категории", "", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(cleared.Success, cleared.Message);
        Assert.StartsWith("PATCH /helix/channels?broadcaster_id=777 ", twitch.Requests[0]);
        Assert.EndsWith("body={\"title\":\"\\u041D\\u043E\\u0432\\u044B\\u0439 \\u0441\\u0442\\u0440\\u0438\\u043C\",\"game_id\":\"491487\"}", twitch.Requests[0]);
        Assert.EndsWith("\"game_id\":\"\"}", twitch.Requests[1]);
    }

    [Fact]
    public async Task Without_the_right_or_with_a_bad_title_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        var noRight = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));
        var ready = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        Assert.Contains("переподключи Twitch", (await noRight.UpdateChannelInfoAsync("Стрим", "", CancellationToken.None)).Message);
        Assert.Equal("Название не может быть пустым.", (await ready.UpdateChannelInfoAsync("   ", "", CancellationToken.None)).Message);
        Assert.Contains("длиннее 140", (await ready.UpdateChannelInfoAsync(new string('а', 141), "", CancellationToken.None)).Message);
        Assert.Empty(twitch.Requests);
    }

    [Theory]
    [InlineData(400, "game_id is invalid", "Twitch не нашёл такую категорию, выбери её из списка.")]
    [InlineData(400, "Title too long", "Twitch отказал: Title too long")]
    [InlineData(403, "", "Twitch не разрешил: менять название может только владелец канала.")]
    [InlineData(429, "", "Слишком часто, подожди немного.")]
    [InlineData(500, "", "Twitch: HTTP 500.")]
    public void A_refusal_is_explained(int status, string message, string expected)
    {
        Assert.Equal(expected, TwitchClient.DescribeChannelUpdateFailure((HttpStatusCode)status, message));
    }

    [Fact]
    public async Task Categories_are_searched_by_name_with_their_pictures()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (_, _) => (200, """{"data":[{"box_art_url":"https://static-cdn.jtvnw.net/ttv-boxart/491487-{width}x{height}.jpg","name":"Dead by Daylight","id":"491487"},{"box_art_url":"https://static-cdn.jtvnw.net/ttv-boxart/1-52x72.jpg","name":"Dead Cells","id":"1"},{"name":"","id":"2"}],"pagination":{}}""");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var found = await client.SearchCategoriesAsync("dead by", CancellationToken.None);
        var nothing = await client.SearchCategoriesAsync("   ", CancellationToken.None);

        Assert.Equal(new[]
        {
            new StreamCategory("491487", "Dead by Daylight", "https://static-cdn.jtvnw.net/ttv-boxart/491487-52x72.jpg"),
            new StreamCategory("1", "Dead Cells", "https://static-cdn.jtvnw.net/ttv-boxart/1-52x72.jpg"),
        }, found);
        Assert.Empty(nothing);
        Assert.StartsWith("GET /helix/search/categories?query=dead%20by&first=10 ", Assert.Single(twitch.Requests));
    }

    [Fact]
    public void The_window_checks_the_title_and_that_the_category_was_picked_from_the_list()
    {
        var dbd = new StreamCategory("491487", "Dead by Daylight", "");

        Assert.Equal("17 / 140", StreamInfoRules.Counter("Убийца ночью о3о!"));
        Assert.Null(StreamInfoRules.TitleProblem("Стрим"));
        Assert.NotNull(StreamInfoRules.TitleProblem("  "));
        Assert.Equal(("491487", (string?)null), StreamInfoRules.GameToSave("dead by daylight", dbd));
        Assert.Equal(("", (string?)null), StreamInfoRules.GameToSave("  ", dbd));
        Assert.Equal(((string?)null, StreamInfoRules.PickFromList), StreamInfoRules.GameToSave("Dead by", dbd));
        Assert.Equal(((string?)null, StreamInfoRules.PickFromList), StreamInfoRules.GameToSave("Dead by Daylight", null));
    }

    [Fact]
    public void Saved_titles_are_remembered_newest_first_without_repeats_and_at_most_ten()
    {
        var settings = new AppSettings();
        for (var i = 1; i <= 12; i++) settings.RememberStreamTitle("Стрим " + i);
        settings.RememberStreamTitle("Стрим 5");
        settings.RememberStreamTitle("   ");

        Assert.Equal(10, settings.RecentStreamTitles.Count);
        Assert.Equal("Стрим 5", settings.RecentStreamTitles[0]);
        Assert.Equal("Стрим 12", settings.RecentStreamTitles[1]);
        Assert.Single(settings.RecentStreamTitles, t => t == "Стрим 5");
        Assert.DoesNotContain("Стрим 1", settings.RecentStreamTitles);
        Assert.Contains(nameof(AppSettings.RecentStreamTitles), Profile.SharedSettings);
    }
}
