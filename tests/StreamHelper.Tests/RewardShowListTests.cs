using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RewardShowListTests
{
    private static readonly RewardInfo[] Channel =
    {
        new("orig-a", "Альфа", 100, true),
        new("copy-a", "Альфа\u200B", 100, true),
        new("orig-b", "Бета", 200, false),
        new("copy-b", "Бета\u200B", 200, false),
        new("solo", "Своя", 50, true),
    };

    private static readonly HashSet<string> Managed = new() { "copy-a", "copy-b" };

    private static AppSettings Settings() => new()
    {
        ManagedRewardIds = Managed.ToList(),
        RewardCopies = new Dictionary<string, string> { ["orig-a"] = "copy-a", ["orig-b"] = "copy-b" },
    };

    [Fact]
    public void With_only_own_rewards_the_list_holds_the_programs_rewards_all_ticked_and_unmarked()
    {
        var settings = Settings();
        settings.OnlyOwnRewards = true;

        var rows = RewardShowList.Build(Channel, Managed, settings, canManage: true);

        Assert.Equal(new[] { "copy-a", "copy-b" }, rows.Select(r => r.Id).ToArray());
        Assert.All(rows, r => Assert.True(r.Shown));
        Assert.Equal("Альфа · 100 баллов", rows[0].DisplayText);
        Assert.Equal("Бета · 200 баллов · выключена", rows[1].DisplayText);
    }

    [Fact]
    public void Without_it_every_reward_is_its_own_line_and_the_ones_not_made_by_the_program_are_marked()
    {
        var rows = RewardShowList.Build(Channel, Managed, Settings(), canManage: true);

        Assert.Equal(new[] { "solo", "orig-a", "copy-a", "orig-b", "copy-b" }, rows.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { true, true, false, true, false }, rows.Select(r => r.TwitchMade).ToArray());
        Assert.Equal(new[] { false, false, true, false, true }, rows.Select(r => r.CanManage).ToArray());
        Assert.Equal("Своя (Создано Twitch) · 50 баллов", rows[0].DisplayText);
        Assert.Equal("Альфа (Создано Twitch) · 100 баллов", rows[1].DisplayText);
        Assert.Equal("Альфа · 100 баллов", rows[2].DisplayText);
        Assert.Equal("Бета (Создано Twitch) · 200 баллов · выключена", rows[3].DisplayText);
    }

    [Fact]
    public void Unticking_a_line_takes_only_that_reward_out_of_the_list_and_ticking_brings_it_back()
    {
        var settings = Settings();
        var rows = RewardShowList.Build(Channel, Managed, settings, canManage: true);
        var original = rows.Single(r => r.Id == "orig-a");

        RewardShowList.Apply(settings, original, false);
        Assert.Equal(new[] { "orig-a" }, settings.HiddenRewardIds.ToArray());
        Assert.True(settings.AllowsReward("orig-a"));
        Assert.False(settings.ListsReward("orig-a"));
        Assert.True(settings.ListsReward("copy-a"));

        RewardShowList.Apply(settings, original, false);
        Assert.Single(settings.HiddenRewardIds);

        RewardShowList.Apply(settings, original, true);
        Assert.Empty(settings.HiddenRewardIds);
    }

    [Fact]
    public void The_ticks_survive_switching_only_own_rewards_on_and_off()
    {
        var settings = Settings();
        settings.HiddenRewardIds.AddRange(new[] { "copy-b", "solo" });

        var all = RewardShowList.Build(Channel, Managed, settings, canManage: true);
        Assert.Equal(new[] { false, true, true, true, false }, all.Select(r => r.Shown).ToArray());

        settings.OnlyOwnRewards = true;
        var own = RewardShowList.Build(Channel, Managed, settings, canManage: true);
        Assert.Equal(new[] { true, false }, own.Select(r => r.Shown).ToArray());
    }

    [Fact]
    public void Without_the_manage_right_no_line_can_be_switched_or_deleted_and_the_waiting_orders_are_counted()
    {
        var rows = RewardShowList.Build(Channel, Managed, Settings(), canManage: false, id => id == "copy-a" ? 3 : 0);

        Assert.All(rows, r => Assert.False(r.CanManage));
        Assert.Equal(3, rows.Single(r => r.Id == "copy-a").PendingCount);
        Assert.Equal(0, rows.Single(r => r.Id == "orig-a").PendingCount);
    }

    [Fact]
    public void A_new_reward_on_the_channel_is_ticked_until_it_is_unticked()
    {
        var settings = Settings();
        var later = Channel.Append(new RewardInfo("new", "Новая", 10, true)).ToArray();

        var rows = RewardShowList.Build(later, Managed, settings, canManage: true);

        Assert.True(rows.Single(r => r.Id == "new").Shown);
        Assert.True(settings.ListsReward("new"));
    }
}
public class RewardToggleRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "channel:read:redemptions channel:manage:redemptions")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Theory]
    [InlineData(false, "{\"is_enabled\":false}")]
    [InlineData(true, "{\"is_enabled\":true}")]
    public async Task Sends_a_patch_of_the_reward_with_only_its_on_off_state(bool enabled, string expectedBody)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        string? body = null;
        da.Handler = (req, b) =>
        {
            Assert.Equal("PATCH", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            body = b;
            return (200, "{\"data\":[{\"id\":\"reward-7\"}]}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SetRewardEnabledAsync("reward-7", enabled, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("PATCH /helix/channel_points/custom_rewards?broadcaster_id=777&id=reward-7", Assert.Single(da.Requests));
        Assert.Equal(expectedBody, body);
    }

    [Theory]
    [InlineData(404, "нет на канале")]
    [InlineData(403, "только там, где её создали")]
    [InlineData(500, "HTTP 500")]
    public async Task A_refusal_is_explained(int status, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"why\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SetRewardEnabledAsync("r", false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task Without_the_manage_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "channel:read:redemptions"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.SetRewardEnabledAsync("r", false, CancellationToken.None));
        Assert.Empty(da.Requests);
    }
}