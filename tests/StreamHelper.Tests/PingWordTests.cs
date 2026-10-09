using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class PingWordTests
{
    private static readonly string[] Bots = { "nightbot" };

    private static bool Ping(string text, string chatter = "viewer", params string[] words) =>
        PingDetector.IsPing(ChatJson.Message("42", chatter, text), "777", "streamer", Bots, words);

    [Fact]
    public void A_chosen_word_makes_a_message_a_ping_in_any_letter_case_and_in_any_language()
    {
        Assert.True(Ping("привет всем", "viewer", "привет"));
        Assert.True(Ping("ПРИВЕТ всем", "viewer", "привет"));
        Assert.True(Ping("Hello there", "viewer", "HELLO"));
        Assert.True(Ping("а как же ёлка?", "viewer", "ЁЛКА"));
        Assert.False(Ping("пока всем", "viewer", "привет"));
    }

    [Fact]
    public void The_word_has_to_be_a_whole_word()
    {
        Assert.False(Ping("котлета на ужин", "viewer", "кот"));
        Assert.False(Ping("мой скот", "viewer", "кот"));
        Assert.False(Ping("cat_1 and cat2", "viewer", "cat"));
        Assert.True(Ping("мой кот!", "viewer", "кот"));
        Assert.True(Ping("кот, кот, кот", "viewer", "кот"));
        Assert.True(Ping("(кот)", "viewer", "кот"));
        Assert.True(Ping("кот", "viewer", "кот"));
    }

    [Fact]
    public void A_star_stands_for_any_letters_and_a_lone_star_does_nothing()
    {
        Assert.True(Ping("все стримы лучше", "viewer", "стрим*"));
        Assert.True(Ping("надо стримить", "viewer", "стрим*"));
        Assert.True(Ping("стрим", "viewer", "стрим*"));
        Assert.False(Ping("мультстрим", "viewer", "стрим*"));
        Assert.True(Ping("мультстрим", "viewer", "*стрим"));
        Assert.True(Ping("пре-стрим", "viewer", "*стрим*"));
        Assert.False(Ping("что угодно", "viewer", "*"));
        Assert.False(Ping("что угодно", "viewer", "**", "  ", ""));
    }

    [Fact]
    public void Words_with_spaces_are_phrases_and_the_spaces_may_be_any_whitespace()
    {
        Assert.True(Ping("всем добрый вечер!", "viewer", "добрый вечер"));
        Assert.True(Ping("добрый   вечер", "viewer", "добрый вечер"));
        Assert.True(Ping("добрый\\tвечер", "viewer", "добрый вечер"));
        Assert.False(Ping("добрый день и вечер", "viewer", "добрый вечер"));
    }

    [Fact]
    public void Characters_that_mean_something_in_a_pattern_are_taken_as_they_are()
    {
        Assert.True(Ping("кто тут? я!", "viewer", "кто тут?"));
        Assert.True(Ping("1+1=2", "viewer", "1+1=2"));
        Assert.True(Ping("цена (10$)", "viewer", "(10$)"));
        Assert.False(Ping("кто тут", "viewer", "кто тут."));
        Assert.True(Ping("a.b", "viewer", "a.b"));
        Assert.False(Ping("axb", "viewer", "a.b"));
    }

    [Fact]
    public void Several_words_any_one_of_them_is_enough_and_the_name_mention_still_works_beside_them()
    {
        Assert.True(Ping("где стример", "viewer", "привет", "стример"));
        Assert.True(Ping("эй @streamer", "viewer", "привет"));
        Assert.True(Ping("эй @streamer", "viewer"));
        Assert.False(Ping("ничего особенного", "viewer", "привет", "стример"));
        Assert.False(Ping("привет", "viewer"));
    }

    [Fact]
    public void The_streamer_and_the_bots_still_never_count_even_when_their_message_has_a_word()
    {
        Assert.False(PingDetector.IsPing(ChatJson.Message("777", "streamer", "привет"), "777", "streamer", Bots, new[] { "привет" }));
        Assert.False(Ping("привет", "nightbot", "привет"));
        Assert.False(Ping("привет", "NightBot", "привет"));
    }

    [Fact]
    public void Without_any_identity_a_word_does_not_make_a_ping_either()
    {
        Assert.False(PingDetector.IsPing(ChatJson.Message("42", "viewer", "привет"), "", "", Bots, new[] { "привет" }));
    }

    [Theory]
    [InlineData("привет, добрый вечер; стрим*", "привет|добрый вечер|стрим*")]
    [InlineData("  привет  \n\n  добрый    вечер  ", "привет|добрый вечер")]
    [InlineData("Кот, кот, КОТ", "Кот")]
    [InlineData("*, **, , ;", "")]
    [InlineData("", "")]
    [InlineData("a b c", "a b c")]
    public void The_list_is_read_leniently(string text, string expected) =>
        Assert.Equal(expected, string.Join("|", PingDetector.ParseWords(text)));

    [Fact]
    public void The_list_and_every_entry_have_a_size_limit()
    {
        var many = string.Join(",", Enumerable.Range(0, PingDetector.MaxWords + 20).Select(i => "слово" + i));
        Assert.Equal(PingDetector.MaxWords, PingDetector.ParseWords(many).Count);

        var long1 = PingDetector.ParseWords(new string('я', PingDetector.MaxWordLength + 40));
        Assert.Equal(PingDetector.MaxWordLength, Assert.Single(long1).Length);
    }

    [Fact]
    public void Nothing_is_chosen_by_default_and_the_choice_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        Assert.Empty(store.Current.PingWords);
        store.Current.PingWords = new List<string> { "привет", "стрим*" };
        store.Save();

        Assert.Equal(new[] { "привет", "стрим*" }, new SettingsStore(dir.File("settings.json")).Current.PingWords.ToArray());
    }
}