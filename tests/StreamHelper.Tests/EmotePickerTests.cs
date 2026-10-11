using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class EmotePickerTests
{
    private static string Url(TwitchEmote e) => "tw:" + e.Id;

    private static readonly TwitchEmote[] Mine =
    {
        new("1", "streamerHi", false, "subscriptions", "777"),
        new("2", "otherLul", true, "subscriptions", "555"),
        new("3", "Kappa", false, "globals", ""),
        new("4", "PrimeYouDontSay", false, "prime", "0"),
        new("5", "streamerHi", false, "follower", "777"),
    };

    [Fact]
    public void Twitch_emote_lists_are_read_with_their_owner_kind_animation_and_next_page()
    {
        var (emotes, cursor) = TwitchClient.ParseEmotes(
            "{\"data\":[{\"id\":\"25\",\"name\":\"Kappa\",\"format\":[\"static\"],\"emote_type\":\"globals\",\"owner_id\":\"0\"}," +
            "{\"id\":\"e1\",\"name\":\"catJAM\",\"format\":[\"static\",\"animated\"],\"emote_type\":\"subscriptions\",\"owner_id\":\"555\"}," +
            "{\"id\":\"\",\"name\":\"broken\"},{\"id\":\"x\",\"name\":\"\"}],\"template\":\"t\",\"pagination\":{\"cursor\":\"abc\"}}");

        Assert.Equal(new[] { "Kappa", "catJAM" }, emotes.Select(e => e.Name).ToArray());
        Assert.False(emotes[0].Animated);
        Assert.True(emotes[1].Animated);
        Assert.Equal(("subscriptions", "555"), (emotes[1].Type, emotes[1].OwnerId));
        Assert.Equal("abc", cursor);
        Assert.Null(TwitchClient.ParseEmotes("{\"data\":[],\"pagination\":{}}").Cursor);
    }

    [Fact]
    public void With_own_emotes_the_channel_comes_first_then_other_channels_then_twitch_then_7tv()
    {
        var all = EmoteChoices.Collect(Mine, Array.Empty<TwitchEmote>(), new[] { new TwitchEmote("25", "Kappa", false), new TwitchEmote("26", "LUL", false) },
            new[] { new ExtraEmote("catJAM", "7tv:cat", true) }, new[] { new ExtraEmote("PETPET", "7tv:pet", true), new ExtraEmote("Kappa", "7tv:kappa", false) }, "777", Url);

        Assert.Equal(
            new[] { ("streamerHi", EmoteSection.Channel), ("catJAM", EmoteSection.Channel), ("otherLul", EmoteSection.Mine), ("Kappa", EmoteSection.Twitch), ("PrimeYouDontSay", EmoteSection.Twitch), ("LUL", EmoteSection.Twitch), ("PETPET", EmoteSection.SevenTv) },
            all.Select(e => (e.Name, e.Section)).ToArray());
        Assert.Equal("tw:1", all[0].Url);
        Assert.Equal("7tv:cat", all[1].Url);
    }

    [Fact]
    public void Without_own_emotes_the_channel_list_and_the_global_list_are_used()
    {
        var all = EmoteChoices.Collect(null, new[] { new TwitchEmote("9", "chanEmote", false, "subscriptions") }, new[] { new TwitchEmote("25", "Kappa", false) },
            Array.Empty<ExtraEmote>(), new[] { new ExtraEmote("PETPET", "7tv:pet", true) }, "777", Url);

        Assert.Equal(new[] { ("chanEmote", EmoteSection.Channel), ("Kappa", EmoteSection.Twitch), ("PETPET", EmoteSection.SevenTv) }, all.Select(e => (e.Name, e.Section)).ToArray());
        Assert.DoesNotContain(all, e => e.Section == EmoteSection.Mine);
    }

    [Fact]
    public void Shelves_show_recent_first_and_a_search_shows_one_list_with_starting_matches_first()
    {
        var all = EmoteChoices.Collect(Mine, Array.Empty<TwitchEmote>(), Array.Empty<TwitchEmote>(), Array.Empty<ExtraEmote>(), new[] { new ExtraEmote("hiThere", "7tv:hi", false) }, "777", Url);

        var shelves = EmoteChoices.Shelves(all, new[] { "Kappa", "gone", "streamerHi" }, "");
        Assert.Equal(new[] { "Недавние", "Канал", "Другой канал", "Twitch", "7TV" }, shelves.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "Kappa", "streamerHi" }, shelves[0].Emotes.Select(e => e.Name).ToArray());

        var found = EmoteChoices.Shelves(all, new[] { "Kappa" }, "  HI ");
        var only = Assert.Single(found);
        Assert.Equal("Найдено", only.Title);
        Assert.Equal(new[] { "hiThere", "streamerHi" }, only.Emotes.Select(e => e.Name).ToArray());
        Assert.Empty(EmoteChoices.Shelves(all, null, "zzz"));
        Assert.Empty(EmoteChoices.Shelves(Array.Empty<EmoteChoice>(), null, ""));
    }

    [Fact]
    public void Colon_suggestions_match_from_two_letters_and_keep_the_case_of_the_emote()
    {
        var all = EmoteChoices.Collect(Mine, Array.Empty<TwitchEmote>(), Array.Empty<TwitchEmote>(), Array.Empty<ExtraEmote>(), Array.Empty<ExtraEmote>(), "777", Url);

        Assert.Equal(new[] { "otherLul" }, EmoteChoices.Match(all, "LU").Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "Kappa" }, EmoteChoices.Match(all, "kap").Select(e => e.Name).ToArray());
        Assert.Empty(EmoteChoices.Match(all, ""));
        Assert.Equal(2, EmoteChoices.Match(all, "e", max: 2).Count);

        Assert.Equal(new MentionToken(7, 10, "ka"), MentionText.EmoteAt("привет :ka", 10));
        Assert.Null(MentionText.EmoteAt("привет :k", 9));
        Assert.Null(MentionText.EmoteAt("a:kap", 5));
        Assert.Null(MentionText.EmoteAt("::kap", 5));
        Assert.Null(MentionText.EmoteAt("время 12:30", 11));
        Assert.Equal(("привет Kappa ", 13), MentionText.Replace("привет :ka", MentionText.EmoteAt("привет :ka", 10)!.Value, "Kappa"));
    }

    [Fact]
    public void Picked_emotes_go_to_the_front_of_recent_without_repeats_and_with_a_limit()
    {
        var recent = EmoteChoices.Remember(new[] { "a", "b", "c" }, "b");
        Assert.Equal(new[] { "b", "a", "c" }, recent);
        Assert.Equal(new[] { "x" }, EmoteChoices.Remember(null, "x"));
        var many = Enumerable.Range(0, 40).Select(i => "e" + i).ToList();
        Assert.Equal(EmoteChoices.RecentLimit, EmoteChoices.Remember(many, "new").Count);
        Assert.Equal("new", EmoteChoices.Remember(many, "new")[0]);
    }

    [Theory]
    [InlineData("", 0, "Kappa ", 6)]
    [InlineData("привет", 6, "привет Kappa ", 13)]
    [InlineData("привет ", 7, "привет Kappa ", 13)]
    [InlineData("ab", 1, "a Kappa b", 8)]
    [InlineData("a b", 1, "a Kappa b", 7)]
    public void An_emote_is_put_at_the_caret_with_spaces_around_it(string text, int caret, string expected, int expectedCaret) =>
        Assert.Equal((expected, expectedCaret), EmoteChoices.InsertAt(text, caret, "Kappa"));
}

public class ChannelStreamerMentionTests
{
    [Fact]
    public void The_streamer_of_the_chat_comes_first_even_before_writing_and_keeps_their_colour_once_seen()
    {
        var chatters = new RecentChatters();
        chatters.Saw("alice", "Alice", "#FF0000");
        var owner = new Chatter("bigstreamer", "BigStreamer", "");

        Assert.Equal(new[] { "bigstreamer", "alice" }, chatters.Match("", "me", pinned: owner).Select(c => c.Login).ToArray());
        Assert.Equal(new[] { "bigstreamer" }, chatters.Match("bi", "me", pinned: owner).Select(c => c.Login).ToArray());
        Assert.Equal(new[] { "alice" }, chatters.Match("al", "me", pinned: owner).Select(c => c.Login).ToArray());

        chatters.Saw("bigstreamer", "BigStreamer", "#00FF00");
        chatters.Saw("bob", "Bob", "");
        var list = chatters.Match("", "me", pinned: owner);
        Assert.Equal(new[] { "bigstreamer", "bob", "alice" }, list.Select(c => c.Login).ToArray());
        Assert.Equal("#00FF00", list[0].Color);
        Assert.Equal(new[] { "bob", "alice" }, chatters.Match("", "bigstreamer", pinned: owner).Where(c => c.Login != "bigstreamer").Select(c => c.Login).ToArray());
        Assert.DoesNotContain(chatters.Match("", "BIGSTREAMER", pinned: owner), c => c.Login == "bigstreamer");
    }
}

public class ChatAppearTests
{
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(250, 250)]
    [InlineData(5000, 1000)]
    public void The_appear_time_stays_between_none_and_a_second(int value, int expected) =>
        Assert.Equal(expected, ChatPlacement.ClampAppearMs(value));

    [Fact]
    public void The_appear_time_is_described_in_seconds_and_defaults_to_a_quarter_second()
    {
        Assert.Equal("сразу", ChatPlacement.DescribeAppear(0));
        Assert.Equal("0,25 с", ChatPlacement.DescribeAppear(250));
        Assert.Equal("1 с", ChatPlacement.DescribeAppear(1000));
        Assert.Equal(250, new AppSettings().ChatAppearMs);
        Assert.Contains(nameof(AppSettings.ChatAppearMs), Profile.SharedSettings);
        Assert.Contains(nameof(AppSettings.ChatRecentEmotes), Profile.SharedSettings);
    }
}

public class EmoteLibraryTests
{
    private sealed class Twitch : IEmoteApi
    {
        public List<string> Calls { get; } = new();
        public bool FailUser { get; set; }

        public Task<IReadOnlyList<TwitchEmote>> GetGlobalEmotesAsync(CancellationToken ct)
        {
            Calls.Add("global");
            return Task.FromResult<IReadOnlyList<TwitchEmote>>(new[] { new TwitchEmote("25", "Kappa", false) });
        }

        public Task<IReadOnlyList<TwitchEmote>> GetChannelEmotesAsync(string broadcasterId, CancellationToken ct)
        {
            Calls.Add("channel " + broadcasterId);
            return Task.FromResult<IReadOnlyList<TwitchEmote>>(new[] { new TwitchEmote("9", "chanEmote", false, "subscriptions", broadcasterId) });
        }

        public Task<IReadOnlyList<TwitchEmote>> GetUserEmotesAsync(string? broadcasterId, CancellationToken ct)
        {
            Calls.Add("user " + broadcasterId);
            if (FailUser) throw new HttpRequestException("down");
            return Task.FromResult<IReadOnlyList<TwitchEmote>>(new[] { new TwitchEmote("1", "myEmote", false, "subscriptions", "555"), new TwitchEmote("2", "chanSub", false, "subscriptions", broadcasterId ?? "") });
        }

        public Task<IReadOnlyList<TwitchUser>> GetUsersAsync(IReadOnlyList<string> ids, CancellationToken ct)
        {
            Calls.Add("users " + string.Join(",", ids));
            return Task.FromResult<IReadOnlyList<TwitchUser>>(ids.Select(id => new TwitchUser(id, "login" + id, "Name" + id, "pic" + id)).ToList());
        }
    }

    private sealed class Seven : IExtraEmoteApi
    {
        public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(new[] { new ExtraEmote("PETPET", "7tv:pet", true) });
        public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExtraEmote>>(new[] { new ExtraEmote("catJAM", "7tv:cat", true) });
    }

    private static (EmoteLibrary Library, Twitch Api, SettingsStore Store) Make(TempDir dir, string scopes)
    {
        var store = MakeFollower.Settings(dir);
        store.Current.TwitchScopes = scopes;
        var api = new Twitch();
        return (new EmoteLibrary(api, new ExtraEmoteCatalog(new Seven(), () => store.Current.ChannelId), store, TwitchEndpoints.Default), api, store);
    }

    [Fact]
    public async Task With_the_emotes_right_the_own_emotes_are_asked_for_this_channel()
    {
        using var dir = new TempDir();
        var (library, api, store) = Make(dir, "user:read:chat user:read:emotes");

        await library.EnsureLoadedAsync();

        Assert.Equal(new[] { "user 777", "global", "users 777,555" }, api.Calls);
        Assert.Equal("Name555", library.Owners["555"].Name);
        Assert.Equal(new[] { ("chanSub", EmoteSection.Channel), ("catJAM", EmoteSection.Channel), ("myEmote", EmoteSection.Mine), ("Kappa", EmoteSection.Twitch), ("PETPET", EmoteSection.SevenTv) },
            library.All.Select(e => (e.Name, e.Section)).ToArray());
        await library.EnsureLoadedAsync();
        Assert.Equal(3, api.Calls.Count);
    }

    [Fact]
    public async Task Without_the_right_or_when_twitch_fails_the_channel_and_global_lists_are_used()
    {
        using var dir = new TempDir();
        var (library, api, _) = Make(dir, "user:read:chat");
        await library.EnsureLoadedAsync();
        Assert.Equal(new[] { "channel 777", "global", "users 777" }, api.Calls);
        Assert.Contains(library.All, e => e.Name == "chanEmote" && e.Section == EmoteSection.Channel);

        using var dir2 = new TempDir();
        var (failing, api2, _) = Make(dir2, "user:read:chat user:read:emotes");
        api2.FailUser = true;
        await failing.EnsureLoadedAsync();
        Assert.Equal(new[] { "user 777", "channel 777", "global", "users 777" }, api2.Calls);
        Assert.Contains(failing.All, e => e.Name == "chanEmote");
    }

    [Fact]
    public async Task A_channel_switch_or_a_new_right_loads_the_lists_again()
    {
        using var dir = new TempDir();
        var (library, api, store) = Make(dir, "user:read:chat");
        await library.EnsureLoadedAsync();
        store.Current.TwitchScopes = "user:read:chat user:read:emotes";
        await library.EnsureLoadedAsync();
        Assert.Contains("user 777", api.Calls);

        library.Reset();
        Assert.Empty(library.All);
        await library.EnsureLoadedAsync();
        Assert.Equal(2, api.Calls.Count(c => c == "user 777"));
    }

    [Fact]
    public void The_emotes_right_is_requested_last()
    {
        Assert.EndsWith(" bits:read user:read:emotes moderator:read:chatters", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = "user:read:emotes" }.HasEmotesScope);
        Assert.False(new AppSettings().HasEmotesScope);
    }
}

public class EmotePickerPlacementTests
{
    private static readonly System.Windows.Rect Screen = new(0, 0, 1920, 1040);
    private static readonly System.Windows.Size Size = new(360, 330);

    [Fact]
    public void The_picker_goes_to_the_right_of_the_chat_bottom_aligned_when_there_is_room()
    {
        var spot = EmotePickerPlacement.Place(new System.Windows.Rect(100, 500, 400, 400), Screen, Size);
        Assert.Equal((506.0, 570.0), (spot.X, spot.Y));
    }

    [Fact]
    public void It_goes_left_when_the_chat_is_at_the_right_edge_and_never_leaves_the_screen()
    {
        var spot = EmotePickerPlacement.Place(new System.Windows.Rect(1500, 10, 400, 200), Screen, Size);
        Assert.Equal((1134.0, 0.0), (spot.X, spot.Y));
    }

    [Fact]
    public void A_chat_as_wide_as_the_screen_gets_the_picker_above_then_below_then_over_it()
    {
        var above = EmotePickerPlacement.Place(new System.Windows.Rect(0, 600, 1920, 300), Screen, Size);
        Assert.Equal((1560.0, 264.0), (above.X, above.Y));
        var below = EmotePickerPlacement.Place(new System.Windows.Rect(0, 100, 1920, 300), Screen, Size);
        Assert.Equal((1560.0, 406.0), (below.X, below.Y));
        var over = EmotePickerPlacement.Place(new System.Windows.Rect(0, 0, 1920, 1040), Screen, Size);
        Assert.Equal((1560.0, 710.0), (over.X, over.Y));
    }
}

public class EmoteShelfOwnerTests
{
    [Fact]
    public void Other_channels_get_one_shelf_each_with_their_name_and_picture_and_keys_for_the_sidebar()
    {
        var mine = new[]
        {
            new TwitchEmote("1", "streamerHi", false, "subscriptions", "777"),
            new TwitchEmote("2", "aaaOne", false, "subscriptions", "555"),
            new TwitchEmote("3", "bbbOne", false, "subscriptions", "666"),
            new TwitchEmote("4", "aaaTwo", true, "subscriptions", "555"),
            new TwitchEmote("5", "Kappa", false, "globals", ""),
        };
        var all = EmoteChoices.Collect(mine, Array.Empty<TwitchEmote>(), Array.Empty<TwitchEmote>(), Array.Empty<ExtraEmote>(), new[] { new ExtraEmote("PETPET", "7", true) }, "777", e => e.Id);
        var owners = new Dictionary<string, EmoteOwner> { ["777"] = new("Streamer", "pic777"), ["555"] = new("AaaChannel", "pic555") };

        var shelves = EmoteChoices.Shelves(all, new[] { "Kappa" }, "", owners);

        Assert.Equal(new[] { "recent", "channel", "owner:555", "owner:666", "twitch", "7tv" }, shelves.Select(s => s.Key).ToArray());
        Assert.Equal(new[] { "Недавние", "Канал · Streamer", "AaaChannel", "Другой канал", "Twitch", "7TV" }, shelves.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { null, "pic777", "pic555", null, null, null }, shelves.Select(s => s.IconUrl).ToArray());
        Assert.Equal(new[] { "aaaOne", "aaaTwo" }, shelves[2].Emotes.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "777", "555", "666" }, EmoteChoices.Owners(all).ToArray());
        Assert.Equal("found", Assert.Single(EmoteChoices.Shelves(all, null, "aaa", owners)).Key);
    }

    [Fact]
    public void Twitch_users_are_read_with_their_names_and_pictures()
    {
        var users = TwitchClient.ParseUsers("{\"data\":[{\"id\":\"555\",\"login\":\"aaa\",\"display_name\":\"Aaa\",\"profile_image_url\":\"https://x/p.png\"},{\"id\":\"\",\"login\":\"none\"}]}");

        var only = Assert.Single(users);
        Assert.Equal(("555", "aaa", "Aaa", "https://x/p.png"), (only.Id, only.Login, only.DisplayName, only.ProfileImageUrl));
    }
}