using System.Text.Json;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class RewardDraftTests
{
    private static RewardDraft Valid => new() { Title = "Заказать трек", Cost = "500", Color = "#9146FF" };

    [Fact]
    public void A_simple_draft_becomes_a_reward()
    {
        var (reward, error) = RewardRules.Build(Valid with { Prompt = "  Напиши трек  ", UserInput = true, SkipQueue = true });

        Assert.Equal("", error);
        Assert.Equal(("Заказать трек", 500L, "Напиши трек", "#9146FF"), (reward!.Title, reward.Cost, reward.Prompt, reward.BackgroundColor));
        Assert.True(reward.IsUserInputRequired);
        Assert.True(reward.SkipRequestQueue);
        Assert.True(reward.IsEnabled);
        Assert.Null(reward.MaxPerStream);
        Assert.Null(reward.GlobalCooldownSeconds);
    }

    [Theory]
    [InlineData("", "500", "Название не может быть пустым.")]
    [InlineData("Ок", "0", "Цена — целое число баллов от 1 до 1 000 000 000.")]
    [InlineData("Ок", "абв", "Цена — целое число баллов от 1 до 1 000 000 000.")]
    [InlineData("Ок", "1000000001", "Цена — целое число баллов от 1 до 1 000 000 000.")]
    public void Wrong_title_or_cost_is_explained(string title, string cost, string expected)
    {
        Assert.Equal(expected, RewardRules.Build(Valid with { Title = title, Cost = cost }).Error);
    }

    [Fact]
    public void Limits_and_the_pause_are_checked_and_counted_in_seconds()
    {
        Assert.Contains("за стрим", RewardRules.Build(Valid with { LimitPerStream = true, PerStream = "0" }).Error);
        Assert.Contains("на зрителя", RewardRules.Build(Valid with { LimitPerUser = true, PerUser = "" }).Error);
        Assert.Contains("от 1 секунды до 7 суток", RewardRules.Build(Valid with { Cooldown = true, CooldownValue = "169", CooldownUnit = CooldownUnit.Hours }).Error);
        Assert.Contains("Описание не длиннее 200", RewardRules.Build(Valid with { Prompt = new string('a', 201) }).Error);
        Assert.Contains("#9146FF", RewardRules.Build(Valid with { Color = "purple" }).Error);

        var (reward, _) = RewardRules.Build(Valid with
        {
            Cost = "1 500", LimitPerStream = true, PerStream = "3", LimitPerUser = true, PerUser = "1",
            Cooldown = true, CooldownValue = "5", CooldownUnit = CooldownUnit.Minutes, Color = "ff0000",
        });
        Assert.Equal((1500L, 3, 1, 300, "#FF0000"), (reward!.Cost, reward.MaxPerStream, reward.MaxPerUserPerStream, reward.GlobalCooldownSeconds, reward.BackgroundColor));
        Assert.Equal("", RewardRules.Build(Valid with { Color = "" }).Reward!.BackgroundColor);
    }

    [Fact]
    public void A_reward_fills_the_form_and_comes_back_the_same()
    {
        var reward = new RewardInfo("copy-1", "Alpha\u200B", 250, false)
        {
            Prompt = "текст", BackgroundColor = "#1e69ff", IsUserInputRequired = true, MaxPerStream = 5, GlobalCooldownSeconds = 7200, SkipRequestQueue = true,
        };

        var draft = RewardRules.FromReward(reward);

        Assert.Equal(("Alpha", "250", "#1E69FF", "2", CooldownUnit.Hours), (draft.Title, draft.Cost, draft.Color, draft.CooldownValue, draft.CooldownUnit));
        Assert.True(draft.LimitPerStream);
        Assert.False(draft.LimitPerUser);
        var (back, error) = RewardRules.Build(draft, "copy-1");
        Assert.Equal("", error);
        Assert.Equal((250L, 5, (int?)null, 7200, false), (back!.Cost, back.MaxPerStream, back.MaxPerUserPerStream, back.GlobalCooldownSeconds, back.IsEnabled));
        Assert.Equal(CooldownUnit.Seconds, RewardRules.FromReward(reward with { GlobalCooldownSeconds = 45 }).CooldownUnit);
        Assert.Equal(CooldownUnit.Minutes, RewardRules.FromReward(reward with { GlobalCooldownSeconds = 120 }).CooldownUnit);
    }

    [Fact]
    public void A_title_is_taken_whatever_the_case_and_the_invisible_mark()
    {
        Assert.True(RewardRules.TitleTaken("заказать ТРЕК", new[] { "Заказать трек\u200B" }));
        Assert.False(RewardRules.TitleTaken("Новый", new[] { "Заказать трек" }));
    }
}

public class RewardEditRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "channel:read:redemptions channel:manage:redemptions";
        return settings;
    }

    private static JsonElement Body(string request) => JsonDocument.Parse(request[(request.IndexOf("body=", StringComparison.Ordinal) + 5)..]).RootElement;

    [Fact]
    public async Task A_new_reward_sends_all_its_fields()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"id\":\"new-1\"}]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var (reward, _) = RewardRules.Build(new RewardDraft
        {
            Title = "Трек", Cost = "300", Prompt = "Какой?", UserInput = true, Color = "#00C8AF", LimitPerUser = true, PerUser = "2",
            Cooldown = true, CooldownValue = "30", CooldownUnit = CooldownUnit.Seconds, Enabled = false,
        });

        var result = await client.CreateRewardAsync(reward!, reward!.Title, CancellationToken.None);

        Assert.Equal((RewardCreateOutcome.Created, "new-1"), (result.Outcome, result.Id));
        var body = Body(Assert.Single(da.Requests));
        Assert.Equal("Трек", body.GetProperty("title").GetString());
        Assert.Equal(300, body.GetProperty("cost").GetInt64());
        Assert.False(body.GetProperty("is_enabled").GetBoolean());
        Assert.Equal("Какой?", body.GetProperty("prompt").GetString());
        Assert.Equal("#00C8AF", body.GetProperty("background_color").GetString());
        Assert.True(body.GetProperty("is_max_per_user_per_stream_enabled").GetBoolean());
        Assert.Equal(2, body.GetProperty("max_per_user_per_stream").GetInt32());
        Assert.Equal(30, body.GetProperty("global_cooldown_seconds").GetInt32());
        Assert.False(body.TryGetProperty("is_max_per_stream_enabled", out _));
    }

    [Fact]
    public async Task An_edit_patches_the_fields_and_switches_limits_off_explicitly()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"id\":\"copy-1\"}]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var values = new RewardInfo("copy-1", "Alpha", 900, true) { Prompt = "", MaxPerStream = 4 };

        var result = await client.UpdateRewardAsync("copy-1", values, CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(da.Requests);
        Assert.StartsWith("PATCH /helix/channel_points/custom_rewards?broadcaster_id=777&id=copy-1 ", request);
        var body = Body(request);
        Assert.Equal(900, body.GetProperty("cost").GetInt64());
        Assert.Equal("", body.GetProperty("prompt").GetString());
        Assert.True(body.GetProperty("is_max_per_stream_enabled").GetBoolean());
        Assert.Equal(4, body.GetProperty("max_per_stream").GetInt32());
        Assert.False(body.GetProperty("is_max_per_user_per_stream_enabled").GetBoolean());
        Assert.False(body.GetProperty("is_global_cooldown_enabled").GetBoolean());
        Assert.False(body.TryGetProperty("title", out _));
        Assert.False(body.TryGetProperty("is_enabled", out _));
        Assert.False(body.TryGetProperty("background_color", out _));
    }

    [Theory]
    [InlineData(404, "Этой награды уже нет на канале.")]
    [InlineData(403, "Twitch разрешает менять эту награду только там, где её создали.")]
    [InlineData(400, "Twitch не принял изменения: bad cooldown")]
    public async Task A_refused_edit_is_explained(int status, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"" + (status == 400 ? "bad cooldown" : "") + "\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.UpdateRewardAsync("r", new RewardInfo("r", "R", 1, true), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
    }
}