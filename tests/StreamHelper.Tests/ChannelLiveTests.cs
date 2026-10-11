using StreamHelper.Api;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChannelLiveTests
{
    [Theory]
    [InlineData(0, "0 зрителей")]
    [InlineData(1, "1 зритель")]
    [InlineData(2, "2 зрителя")]
    [InlineData(4, "4 зрителя")]
    [InlineData(5, "5 зрителей")]
    [InlineData(11, "11 зрителей")]
    [InlineData(14, "14 зрителей")]
    [InlineData(21, "21 зритель")]
    [InlineData(22, "22 зрителя")]
    [InlineData(112, "112 зрителей")]
    public void Viewers_are_counted_in_russian(int count, string expected)
    {
        Assert.Equal(expected, ChannelLive.Viewers(count));
    }

    [Fact]
    public void Big_numbers_are_split_into_groups()
    {
        Assert.Equal("1 234 зрителя", ChannelLive.Viewers(1234));
    }

    [Theory]
    [InlineData(0, "только начался")]
    [InlineData(45, "идёт 45 мин")]
    [InlineData(60, "идёт 1 ч")]
    [InlineData(85, "идёт 1 ч 25 мин")]
    [InlineData(610, "идёт 10 ч 10 мин")]
    public void The_length_of_the_stream_is_short(int minutes, string expected)
    {
        Assert.Equal(expected, ChannelLive.Duration(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void The_hint_tells_the_game_the_viewers_the_length_and_the_title()
    {
        var now = new DateTime(2026, 10, 11, 4, 0, 0, DateTimeKind.Utc);
        var stream = new LiveStream("555", "big_streamer", "Just Chatting", " Болтаем ", 1234, now.AddMinutes(-85));

        Assert.Equal("В эфире · Just Chatting · 1 234 зрителя · идёт 1 ч 25 мин\nБолтаем", ChannelLive.Describe(stream, now));
        Assert.Equal("В эфире · 1 зритель", ChannelLive.Describe(stream with { Game = "", Title = "", Viewers = 1, StartedUtc = null }, now));
    }

    [Fact]
    public void Only_channels_that_are_live_count()
    {
        var live = TwitchClient.ParseLiveStreams(
            "{\"data\":[{\"user_id\":\"555\",\"user_login\":\"big_streamer\",\"game_name\":\"Minecraft\",\"title\":\"t\",\"type\":\"live\",\"viewer_count\":12,\"started_at\":\"2026-10-11T02:35:00Z\"}," +
            "{\"user_id\":\"556\",\"type\":\"\"},{\"user_id\":\"\",\"type\":\"live\"}],\"pagination\":{}}");

        var stream = Assert.Single(live);
        Assert.Equal(new LiveStream("555", "big_streamer", "Minecraft", "t", 12, new DateTime(2026, 10, 11, 2, 35, 0, DateTimeKind.Utc)), stream);
        Assert.Equal(DateTimeKind.Utc, stream.StartedUtc!.Value.Kind);
    }

    [Fact]
    public async Task Many_channels_are_asked_a_hundred_at_a_time()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (request, _) => request.QueryString.GetValues("user_id")!.Contains("c150")
            ? (200, "{\"data\":[{\"user_id\":\"c150\",\"type\":\"live\",\"viewer_count\":3}]}")
            : (200, "{\"data\":[]}");
        var client = new TwitchClient(MakeFollower.Settings(dir), new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, TwitchEndpoints.FromBase(da.BaseUrl));
        var ids = Enumerable.Range(1, 150).Select(i => "c" + i).Append("c1").Append("").ToList();

        var live = await client.GetLiveStreamsAsync(ids, CancellationToken.None);

        Assert.Equal("c150", Assert.Single(live).UserId);
        Assert.Equal(2, da.Requests.Count);
        Assert.StartsWith("GET /helix/streams?user_id=c1&user_id=c2&", da.Requests[0]);
        Assert.Contains("&user_id=c100&first=100 ", da.Requests[0]);
        Assert.StartsWith("GET /helix/streams?user_id=c101&", da.Requests[1]);
    }
}
