using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class PingHistoryTests
{
    private static ChatMessage Said(string id, string chatterId, string login, string text, int minutes, string color = "") =>
        new(id, chatterId, login, login, text, Array.Empty<ChatMention>(), MakeFollower.T0.AddMinutes(minutes), color);

    private static ChatFeed Feed(params ChatMessage[] messages)
    {
        var feed = new ChatFeed();
        foreach (var message in messages) feed.Push(message);
        return feed;
    }

    [Fact]
    public void The_latest_message_of_a_person_is_found_by_id_or_by_login_in_any_case()
    {
        var feed = Feed(Said("m1", "42", "alice", "раз", 0), Said("m2", "43", "bob", "два", 1), Said("m3", "42", "alice", "три", 2));

        Assert.Equal("m3", feed.LatestFrom("42", "")!.MessageId);
        Assert.Equal("m3", feed.LatestFrom("", "ALICE")!.MessageId);
        Assert.Equal("m2", feed.LatestFrom("", "bob")!.MessageId);
        Assert.Null(feed.LatestFrom("99", "nobody"));
    }

    [Fact]
    public void A_ping_opens_the_history_of_its_author_with_the_colour_from_the_chat()
    {
        var feed = Feed(Said("m1", "42", "alice", "@streamer привет", 0, "#FF0000"), Said("m2", "42", "alice", "ещё", 1, "#FF0000"));
        var ping = new ChatPing { Key = "m1", ChatterId = "42", Login = "alice", DisplayName = "Alice", Message = "@streamer привет" };

        var sample = ChatOverlayService.HistorySample(feed, ping);

        Assert.Equal(("42", "#FF0000"), (sample.ChatterId, sample.Color));
        Assert.Equal(new[] { "m1", "m2" }, feed.HistoryOf(ChatFeed.KeyOf(sample)).Select(m => m.MessageId));
    }

    [Fact]
    public void An_old_ping_without_the_author_id_still_finds_the_history_by_login()
    {
        var feed = Feed(Said("m1", "42", "alice", "@streamer привет", 0));
        var ping = new ChatPing { Key = "m1", Login = "Alice", DisplayName = "Alice", Message = "@streamer привет" };

        var sample = ChatOverlayService.HistorySample(feed, ping);

        Assert.Equal("42", ChatFeed.KeyOf(sample));
        Assert.Single(feed.HistoryOf(ChatFeed.KeyOf(sample)));
    }

    [Fact]
    public void Without_messages_in_the_chat_the_history_is_opened_from_the_ping_itself()
    {
        var ping = new ChatPing { Key = "m9", ChatterId = "42", Login = "alice", DisplayName = "Alice", Message = "@streamer ты тут?", AtUtc = MakeFollower.T0 };

        var sample = ChatOverlayService.HistorySample(new ChatFeed(), ping);

        Assert.Equal(("m9", "42", "alice", "Alice", "@streamer ты тут?"), (sample.MessageId, sample.ChatterId, sample.ChatterLogin, sample.ChatterName, sample.Text));
        Assert.Equal(MakeFollower.T0, sample.AtUtc);
    }
}
