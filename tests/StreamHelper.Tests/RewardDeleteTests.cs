using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class RewardDeleteRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "channel:read:redemptions channel:manage:redemptions")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Fact]
    public async Task Sends_a_delete_for_the_reward_of_the_channel_with_the_bearer_token()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            Assert.Equal("DELETE", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.Equal("twitch-client", req.Headers["Client-Id"]);
            return (204, "");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.DeleteRewardAsync("reward-7", CancellationToken.None);

        Assert.Equal(RewardDeleteOutcome.Deleted, result.Outcome);
        Assert.Contains("DELETE /helix/channel_points/custom_rewards?broadcaster_id=777&id=reward-7", Assert.Single(da.Requests));
    }

    [Theory]
    [InlineData(404, RewardDeleteOutcome.NotFound)]
    [InlineData(403, RewardDeleteOutcome.NotAllowed)]
    [InlineData(500, RewardDeleteOutcome.Failed)]
    public async Task Twitch_answers_are_mapped_to_outcomes(int status, RewardDeleteOutcome expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"why\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal(expected, (await client.DeleteRewardAsync("r", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Without_the_manage_right_nothing_is_sent_and_a_401_refreshes_once()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var noRight = new TwitchClient(Ready(dir, "channel:read:redemptions"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        await Assert.ThrowsAsync<AuthRequiredException>(() => noRight.DeleteRewardAsync("r", CancellationToken.None));
        Assert.Empty(da.Requests);

        da.Handler = (req, _) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000}");
            return req.Headers["Authorization"] == "Bearer tw-access-2" ? (204, "") : (401, "{}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal(RewardDeleteOutcome.Deleted, (await client.DeleteRewardAsync("r", CancellationToken.None)).Outcome);
        Assert.Equal(3, da.Requests.Count);
    }
}

public class RewardCleanupTests
{
    private static RewardInfo R(string id, string title = "T") => new(id, title, 100, true);

    [Fact]
    public async Task Deletes_what_it_is_given_and_reports_the_rest()
    {
        var api = new FakeRewardApi
        {
            OnDelete = id => id switch
            {
                "gone" => new RewardDeleteResult(RewardDeleteOutcome.NotFound, "x"),
                "foreign" => new RewardDeleteResult(RewardDeleteOutcome.NotAllowed, "x"),
                "broken" => new RewardDeleteResult(RewardDeleteOutcome.Failed, "HTTP 500"),
                _ => new RewardDeleteResult(RewardDeleteOutcome.Deleted, ""),
            },
        };

        var result = await RewardCleanup.DeleteAsync(api,
            new[] { R("ok", "Трек​"), R("gone"), R("foreign", "Чужая"), R("broken", "Сломанная") }, CancellationToken.None);

        Assert.Equal(new[] { "ok", "gone" }, result.DeletedIds.ToArray());
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, f => f.StartsWith("Чужая:") && f.Contains("не через программу"));
        Assert.Contains(result.Failures, f => f.StartsWith("Сломанная:") && f.Contains("HTTP 500"));
    }

    [Fact]
    public void Deleting_forgets_the_reward_everywhere_and_closes_the_pending_redemptions()
    {
        using var dir = new TempDir();
        var settings = new AppSettings
        {
            ManagedRewardIds = new List<string> { "copy-a", "copy-b" },
            RewardCopies = new Dictionary<string, string> { ["orig-a"] = "copy-a", ["orig-b"] = "copy-b" },
        };
        var store = new RedemptionStore(dir.File("redemptions.json"));
        var pending = new Redemption { Key = "p", RewardId = "copy-a", CanManage = true };
        var accepted = new Redemption { Key = "d", RewardId = "copy-a", CanManage = true, Status = RedemptionStatus.Accepted };
        var other = new Redemption { Key = "o", RewardId = "copy-b", CanManage = true };
        store.AddRange(new[] { pending, accepted, other });

        RewardCleanup.ApplyLocally(settings, store, new[] { "copy-a" });

        Assert.Equal(new[] { "copy-b" }, settings.ManagedRewardIds.ToArray());
        Assert.Equal(new[] { "orig-b" }, settings.RewardCopies.Keys.ToArray());
        Assert.Equal(RedemptionStatus.Processed, pending.Status);
        Assert.False(pending.CanManage);
        Assert.Equal(RedemptionStatus.Accepted, accepted.Status);
        Assert.False(accepted.CanManage);
        Assert.Equal(RedemptionStatus.Pending, other.Status);
        Assert.True(other.CanManage);
        Assert.False(pending.ShowActions);
    }

    [Fact]
    public void Nothing_deleted_changes_nothing()
    {
        using var dir = new TempDir();
        var settings = new AppSettings { ManagedRewardIds = new List<string> { "a" } };
        var store = new RedemptionStore(dir.File("redemptions.json"));

        RewardCleanup.ApplyLocally(settings, store, Array.Empty<string>());

        Assert.Equal(new[] { "a" }, settings.ManagedRewardIds.ToArray());
    }

    [Fact]
    public void The_row_text_warns_about_pending_orders_and_names_the_reward_without_the_invisible_mark()
    {
        var plain = new RewardRow("a", "Заказать трек​", 500, enabled: true, own: true, canManage: true, pendingCount: 0, shown: true);
        var busy = new RewardRow("b", "Эмодзи", 100, enabled: true, own: true, canManage: true, pendingCount: 3, shown: true);

        Assert.Equal("Заказать трек · 500 баллов", plain.DisplayText);
        Assert.Equal("Удалить «Заказать трек»?", plain.ConfirmText);
        Assert.Contains("необработанных заказов: 3", busy.DisplayText);
        Assert.Contains("засчитает как выполненные, баллы не вернутся", busy.ConfirmText);

        var raised = new List<string?>();
        busy.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        busy.Confirming = true;
        Assert.False(busy.NotConfirming);
        Assert.Contains(nameof(RewardRow.NotConfirming), raised);
    }
}
