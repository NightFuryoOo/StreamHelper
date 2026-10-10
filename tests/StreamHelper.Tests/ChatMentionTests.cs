using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatMentionTests
{
    private static RecentChatters Chatters()
    {
        var chatters = new RecentChatters();
        chatters.Saw("alice", "Alice", "#FF0000");
        chatters.Saw("bob_42", "Bob_42", "");
        chatters.Saw("streamer", "Streamer", "");
        chatters.Saw("kotik", "Котик", "");
        chatters.Saw("malice", "Malice", "");
        return chatters;
    }

    [Fact]
    public void An_at_sign_alone_lists_the_latest_chatters_first_without_yourself()
    {
        var names = Chatters().Match("", "streamer").Select(c => c.Login).ToArray();

        Assert.Equal(new[] { "malice", "kotik", "bob_42", "alice" }, names);
    }

    [Fact]
    public void Typed_letters_narrow_the_list_starting_matches_first_and_any_case()
    {
        var chatters = Chatters();

        Assert.Equal(new[] { "alice", "malice" }, chatters.Match("AL", "streamer").Select(c => c.Login).ToArray());
        Assert.Equal(new[] { "kotik" }, chatters.Match("кот", "streamer").Select(c => c.Login).ToArray());
        Assert.Empty(chatters.Match("zzz", "streamer"));
        Assert.Equal(2, chatters.Match("", null, max: 2).Count);
    }

    [Fact]
    public void Writing_again_moves_a_chatter_to_the_top_and_the_list_has_a_limit()
    {
        var chatters = Chatters();
        chatters.Saw("ALICE", "Alice", "#00FF00");

        var top = chatters.Match("", "streamer")[0];
        Assert.Equal(("ALICE", "#00FF00"), (top.Login, top.Color));
        Assert.Equal(5, chatters.Count);
        chatters.Saw("", "Nobody", "");
        Assert.Equal(5, chatters.Count);

        for (var i = 0; i < RecentChatters.Limit + 20; i++) chatters.Saw("user" + i, "", "");
        Assert.Equal(RecentChatters.Limit, chatters.Count);
        Assert.Equal("user" + (RecentChatters.Limit + 19), chatters.Match("", null)[0].Login);
        chatters.Clear();
        Assert.Equal(0, chatters.Count);
    }

    [Theory]
    [InlineData("@", 1, 0, 1, "")]
    [InlineData("привет @al", 10, 7, 10, "al")]
    [InlineData("привет @alice как дела", 10, 7, 13, "al")]
    [InlineData("@bo", 3, 0, 3, "bo")]
    public void The_word_with_an_at_sign_under_the_caret_is_found(string text, int caret, int start, int end, string prefix)
    {
        var token = MentionText.At(text, caret);

        Assert.Equal(new MentionToken(start, end, prefix), token);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("привет", 6)]
    [InlineData("mail@example", 12)]
    [InlineData("@alice ", 7)]
    [InlineData("@@x", 3)]
    [InlineData("@alice", 0)]
    [InlineData("@alice", 99)]
    public void Other_places_are_not_mentions(string text, int caret) => Assert.Null(MentionText.At(text, caret));

    [Fact]
    public void Choosing_replaces_the_word_with_the_name_and_a_space()
    {
        var at = MentionText.At("привет @al", 10)!.Value;
        Assert.Equal(("привет @Alice ", 14), MentionText.Insert("привет @al", at, "Alice"));

        var middle = MentionText.At("@al как дела", 2)!.Value;
        Assert.Equal(("@Alice как дела", 7), MentionText.Insert("@al как дела", middle, "Alice"));
    }

    [Fact]
    public void The_inserted_name_is_the_display_name_only_when_it_matches_the_login()
    {
        Assert.Equal("NightFuryo3o", MentionText.NameFor(new Chatter("nightfuryo3o", "NightFuryo3o", "")));
        Assert.Equal("kotik", MentionText.NameFor(new Chatter("kotik", "Котик", "")));
    }
}
