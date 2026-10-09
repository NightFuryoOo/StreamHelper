using System.ComponentModel;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class RewardRenameTests
{
    private const string Mark = RewardSync.InvisibleMark;

    private static FakeRewardApi Api() => new();

    private static Task<RewardRenameRun> Rename(FakeRewardApi api, string current, string name, params string[] others) =>
        RewardRename.RunAsync(api, "copy-1", current, name, others, CancellationToken.None);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(Mark)]
    public async Task An_empty_name_is_refused_without_asking_Twitch(string name)
    {
        var api = Api();

        var result = await Rename(api, "Old", name);

        Assert.False(result.Success);
        Assert.Equal("Название не может быть пустым.", result.Message);
        Assert.Empty(api.Renamed);
    }

    [Fact]
    public async Task A_name_longer_than_Twitch_allows_is_refused()
    {
        var api = Api();

        var result = await Rename(api, "Old", new string('я', RewardSync.TitleLimit + 1));

        Assert.False(result.Success);
        Assert.Contains("не длиннее 45", result.Message);
        Assert.Empty(api.Renamed);
    }

    [Theory]
    [InlineData("Alpha" + Mark, "Alpha")]
    [InlineData("Alpha", "  Alpha  ")]
    public async Task The_same_name_sends_nothing(string current, string typed)
    {
        var api = Api();

        var result = await Rename(api, current, typed, "Alpha");

        Assert.True(result.Success);
        Assert.Equal(current, result.Title);
        Assert.Empty(api.Renamed);
    }

    [Fact]
    public async Task A_free_name_is_sent_as_typed_without_spaces_around()
    {
        var api = Api();

        var result = await Rename(api, "Alpha" + Mark, "  Заказать трек  ", "Alpha", "Beta");

        Assert.True(result.Success);
        Assert.Equal("Заказать трек", result.Title);
        Assert.Equal(("copy-1", "Заказать трек"), Assert.Single(api.Renamed));
    }

    [Theory]
    [InlineData("Beta")]
    [InlineData("beta")]
    public async Task A_name_another_reward_already_has_gets_the_invisible_mark(string typed)
    {
        var api = Api();

        var result = await Rename(api, "Alpha" + Mark, typed, "Alpha", "Beta");

        Assert.True(result.Success);
        Assert.Equal(typed + Mark, result.Title);
        Assert.Equal(typed + Mark, Assert.Single(api.Renamed).Title);
    }

    [Fact]
    public async Task When_both_the_name_and_its_marked_twin_are_taken_nothing_is_sent()
    {
        var api = Api();

        var result = await Rename(api, "Gamma", "Beta", "Beta", "Beta" + Mark);

        Assert.False(result.Success);
        Assert.Equal("Название «Beta» уже занято другой наградой на канале.", result.Message);
        Assert.Empty(api.Renamed);
    }

    [Fact]
    public async Task A_taken_name_of_full_length_cannot_get_the_mark_and_is_refused()
    {
        var api = Api();
        var longName = new string('x', RewardSync.TitleLimit);

        var result = await Rename(api, "Gamma", longName, longName);

        Assert.False(result.Success);
        Assert.Contains("уже занято", result.Message);
        Assert.Empty(api.Renamed);
    }

    [Fact]
    public async Task If_Twitch_still_says_taken_the_marked_name_is_tried_next()
    {
        var api = Api();
        api.OnRename = (_, title) => title == "Delta"
            ? new RewardRenameResult(RewardRenameOutcome.TitleTaken, "CREATE_CUSTOM_REWARD_DUPLICATE_REWARD")
            : new RewardRenameResult(RewardRenameOutcome.Renamed, "");

        var result = await Rename(api, "Gamma", "Delta", "Alpha");

        Assert.True(result.Success);
        Assert.Equal("Delta" + Mark, result.Title);
        Assert.Equal(new[] { "Delta", "Delta" + Mark }, api.Renamed.Select(r => r.Title));
    }

    [Fact]
    public async Task Another_refusal_stops_and_keeps_the_old_title()
    {
        var api = Api();
        api.OnRename = (_, _) => new RewardRenameResult(RewardRenameOutcome.Failed, "Этой награды уже нет на канале.");

        var result = await Rename(api, "Gamma", "Delta");

        Assert.False(result.Success);
        Assert.Equal("Gamma", result.Title);
        Assert.Equal("Этой награды уже нет на канале.", result.Message);
        Assert.Single(api.Renamed);
    }

    [Fact]
    public void A_new_title_updates_everything_the_row_shows()
    {
        var row = new RewardRow("copy-1", "Alpha" + Mark, 100, true, true, true, 0, true);
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        row.Title = "Новое" + Mark;

        Assert.Equal("Новое", row.Name);
        Assert.Equal("Новое · 100 баллов", row.DisplayText);
        Assert.Equal("Переименовать «Новое»", row.RenameName);
        foreach (var name in new[] { "Title", "Name", "DisplayText", "ConfirmText", "SwitchName", "DeleteName", "RenameName" }) Assert.Contains(name, raised);
    }

    [Fact]
    public void Renaming_switches_between_the_text_and_the_box()
    {
        var row = new RewardRow("copy-1", "Alpha", 100, true, true, true, 0, true);

        row.Renaming = true;

        Assert.True(row.Renaming);
        Assert.False(row.NotRenaming);
    }
}

public class RewardRenameRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "channel:read:redemptions channel:manage:redemptions")
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.TwitchAccessToken = "tw-access-1";
        store.Current.TwitchRefreshToken = "tw-refresh-1";
        store.Current.TwitchAccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
        store.Current.TwitchUserId = "777";
        store.Current.TwitchLogin = "streamer";
        store.Current.TwitchScopes = scopes;
        return store;
    }

    [Fact]
    public async Task Sends_a_patch_with_only_the_new_title()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        string? body = null;
        da.Handler = (req, b) =>
        {
            Assert.Equal("PATCH", req.HttpMethod);
            body = b;
            return (200, "{\"data\":[{\"id\":\"reward-7\"}]}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SetRewardTitleAsync("reward-7", "Новое" + RewardSync.InvisibleMark, CancellationToken.None);

        Assert.Equal(RewardRenameOutcome.Renamed, result.Outcome);
        Assert.Contains("PATCH /helix/channel_points/custom_rewards?broadcaster_id=777&id=reward-7", Assert.Single(da.Requests));
        Assert.Equal("Новое" + RewardSync.InvisibleMark, System.Text.Json.JsonDocument.Parse(body!).RootElement.GetProperty("title").GetString());
        Assert.Single(System.Text.Json.JsonDocument.Parse(body!).RootElement.EnumerateObject());
    }

    [Theory]
    [InlineData(400, "{\"message\":\"CREATE_CUSTOM_REWARD_DUPLICATE_REWARD\"}", RewardRenameOutcome.TitleTaken, "DUPLICATE")]
    [InlineData(400, "{\"message\":\"title is too long\"}", RewardRenameOutcome.Failed, "Twitch не принял название: title is too long")]
    [InlineData(404, "{}", RewardRenameOutcome.Failed, "нет на канале")]
    [InlineData(403, "{}", RewardRenameOutcome.Failed, "только там, где её создали")]
    [InlineData(500, "{\"message\":\"boom\"}", RewardRenameOutcome.Failed, "HTTP 500")]
    public async Task Answers_are_sorted_out(int status, string response, RewardRenameOutcome outcome, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, response);
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SetRewardTitleAsync("r", "x", CancellationToken.None);

        Assert.Equal(outcome, result.Outcome);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task Without_the_manage_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "channel:read:redemptions"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.SetRewardTitleAsync("r", "x", CancellationToken.None));
        Assert.Empty(da.Requests);
    }
}
