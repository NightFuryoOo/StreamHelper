using StreamHelper.Api;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class ChatHistoryTests
{
    private static ChatMessage Msg(string id, string chatterId, string login, string text, DateTime at) =>
        new(id, chatterId, login, login, text, Array.Empty<ChatMention>(), at);

    [Fact]
    public void The_history_of_a_chatter_is_everything_they_wrote_oldest_first_and_nothing_of_others()
    {
        var feed = new ChatFeed();
        var t = new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
        feed.Push(Msg("1", "42", "viewer", "первое", t));
        feed.Push(Msg("2", "7", "other", "чужое", t.AddMinutes(1)));
        feed.Push(Msg("3", "42", "viewer", "второе", t.AddMinutes(2)));

        var history = feed.HistoryOf("42");

        Assert.Equal(new[] { "первое", "второе" }, history.Select(m => m.Text).ToArray());
        Assert.Empty(feed.HistoryOf("999"));
    }

    [Fact]
    public void Only_what_was_written_since_the_stream_began_is_shown_when_it_is_known()
    {
        var feed = new ChatFeed();
        var start = new DateTime(2026, 10, 9, 18, 0, 0, DateTimeKind.Utc);
        feed.Push(Msg("1", "42", "viewer", "до стрима", start.AddMinutes(-30)));
        feed.Push(Msg("2", "42", "viewer", "ровно в начале", start));
        feed.Push(Msg("3", "42", "viewer", "во время", start.AddMinutes(5)));

        Assert.Equal(new[] { "ровно в начале", "во время" }, feed.HistoryOf("42", start).Select(m => m.Text).ToArray());
        Assert.Equal(3, feed.HistoryOf("42", null).Count);
    }

    [Fact]
    public void A_message_that_arrives_twice_is_in_the_history_once()
    {
        var feed = new ChatFeed();
        var t = DateTime.UtcNow;
        feed.Push(Msg("same", "42", "viewer", "раз", t));
        feed.Push(Msg("same", "42", "viewer", "раз", t));

        Assert.Single(feed.HistoryOf("42"));
    }

    [Fact]
    public void A_chatter_is_known_by_the_id_and_by_the_lower_case_login_when_there_is_no_id()
    {
        Assert.Equal("42", ChatFeed.KeyOf(Msg("1", "42", "Viewer", "x", DateTime.UtcNow)));
        Assert.Equal("viewer", ChatFeed.KeyOf(Msg("1", "", "Viewer", "x", DateTime.UtcNow)));

        var feed = new ChatFeed();
        feed.Push(Msg("1", "", "Viewer", "без id", DateTime.UtcNow));
        feed.Push(Msg("2", "", "VIEWER", "и так же", DateTime.UtcNow));
        Assert.Equal(2, feed.HistoryOf("viewer").Count);
    }

    [Fact]
    public void The_oldest_messages_go_when_the_history_is_full()
    {
        var feed = new ChatFeed();
        var t = DateTime.UtcNow;
        for (var i = 0; i < ChatFeed.KeepHistory + 10; i++) feed.Push(Msg("m" + i, "42", "viewer", "n" + i, t));

        var history = feed.HistoryOf("42");

        Assert.Equal(ChatFeed.KeepHistory, history.Count);
        Assert.Equal("n10", history[0].Text);
        Assert.Equal("n" + (ChatFeed.KeepHistory + 9), history[^1].Text);
    }
}

public class StreamStartTests
{
    [Fact]
    public void A_live_channel_gives_the_time_its_stream_began_in_utc()
    {
        var json = "{\"data\":[{\"id\":\"1\",\"user_id\":\"777\",\"type\":\"live\",\"started_at\":\"2026-10-09T17:03:11Z\"}]}";

        var start = TwitchClient.ParseStreamStart(json);

        Assert.Equal(new DateTime(2026, 10, 9, 17, 3, 11, DateTimeKind.Utc), start);
        Assert.Equal(DateTimeKind.Utc, start!.Value.Kind);
    }

    [Fact]
    public void A_channel_that_is_not_live_or_an_answer_that_makes_no_sense_gives_nothing()
    {
        Assert.Null(TwitchClient.ParseStreamStart("{\"data\":[]}"));
        Assert.Null(TwitchClient.ParseStreamStart("{}"));
        Assert.Null(TwitchClient.ParseStreamStart("not json"));
        Assert.Null(TwitchClient.ParseStreamStart("{\"data\":[{\"started_at\":\"\"}]}"));
    }

    [Fact]
    public async Task The_request_asks_about_the_streamers_channel_and_reads_the_answer()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (_, _) => (200, "{\"data\":[{\"started_at\":\"2026-10-09T17:03:11Z\"}]}");
        var client = new TwitchClient(MakeFollower.Settings(dir), new System.Net.Http.HttpClient(), TwitchEndpoints.FromBase(server.BaseUrl));

        var start = await client.GetStreamStartAsync(CancellationToken.None);

        Assert.Equal(new DateTime(2026, 10, 9, 17, 3, 11, DateTimeKind.Utc), start);
        Assert.Contains("GET /helix/streams?user_id=777", server.Requests[0]);
        Assert.Contains("auth=Bearer", server.Requests[0]);
    }

    [Fact]
    public void The_streams_address_follows_the_base_of_the_other_addresses()
    {
        Assert.Equal("https://api.twitch.tv/helix/streams", TwitchEndpoints.Default.StreamsUrl);
        Assert.Equal("http://127.0.0.1:9/helix/streams", TwitchEndpoints.FromBase("http://127.0.0.1:9").StreamsUrl);
    }
}