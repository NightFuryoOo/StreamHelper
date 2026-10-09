using System.Net.Http;
using System.Text.Json;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal sealed class FakeRewardApi : IRewardApi
{
    public List<RewardInfo> Rewards { get; } = new();
    public HashSet<string> Managed { get; } = new();
    public List<(RewardInfo Template, string Title)> Created { get; } = new();
    public List<(string RewardId, string RedemptionId, RedemptionDecision Decision)> Updates { get; } = new();
    public Func<RewardInfo, string, RewardCreateResult>? OnCreate { get; set; }
    public RedemptionUpdateResult UpdateResult { get; set; } = new(RedemptionUpdateOutcome.Done, "");
    public Func<string, RedemptionUpdateResult>? OnUpdate { get; set; }
    public Dictionary<string, DownloadedImage?> Images { get; } = new();
    public List<string> Downloads { get; } = new();
    private int _nextId = 1;

    public List<string> Deleted { get; } = new();
    public Func<string, RewardDeleteResult>? OnDelete { get; set; }

    public Task<RewardDeleteResult> DeleteRewardAsync(string rewardId, CancellationToken ct)
    {
        var result = OnDelete?.Invoke(rewardId) ?? new RewardDeleteResult(RewardDeleteOutcome.Deleted, "");
        if (result.Outcome == RewardDeleteOutcome.Deleted)
        {
            Deleted.Add(rewardId);
            Rewards.RemoveAll(r => r.Id == rewardId);
            Managed.Remove(rewardId);
        }
        return Task.FromResult(result);
    }

    public Task<DownloadedImage?> DownloadImageAsync(string url, CancellationToken ct)
    {
        Downloads.Add(url);
        if (url.Contains("boom")) throw new HttpRequestException("network down");
        return Task.FromResult(Images.TryGetValue(url, out var image) ? image : null);
    }

    public Task<IReadOnlyList<RewardInfo>> GetRewardsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RewardInfo>>(Rewards.ToList());

    public Task<IReadOnlySet<string>> GetManageableRewardIdsAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<string>>(Managed.ToHashSet());

    public Task<RewardCreateResult> CreateRewardAsync(RewardInfo template, string title, CancellationToken ct)
    {
        Created.Add((template, title));
        var result = OnCreate?.Invoke(template, title) ?? new RewardCreateResult(RewardCreateOutcome.Created, "copy-" + _nextId++, "");
        if (result.Outcome == RewardCreateOutcome.Created && result.Id != null)
        {
            Rewards.Add(template with { Id = result.Id, Title = title });
            Managed.Add(result.Id);
        }
        return Task.FromResult(result);
    }

    public List<(string RewardId, bool Enabled)> Switched { get; } = new();

    public Task<RewardToggleResult> SetRewardEnabledAsync(string rewardId, bool enabled, CancellationToken ct)
    {
        Switched.Add((rewardId, enabled));
        return Task.FromResult(new RewardToggleResult(true, ""));
    }

    public Task<RedemptionUpdateResult> UpdateRedemptionAsync(string rewardId, string redemptionId, RedemptionDecision decision, CancellationToken ct)
    {
        Updates.Add((rewardId, redemptionId, decision));
        return Task.FromResult(OnUpdate?.Invoke(redemptionId) ?? UpdateResult);
    }
}

public class RewardRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "moderator:read:followers channel:read:subscriptions channel:read:redemptions channel:manage:redemptions")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private const string FullRewardJson =
        "{\"data\":[{\"id\":\"r1\",\"title\":\"Заказать трек\",\"cost\":500,\"prompt\":\"Название трека\",\"is_enabled\":true," +
        "\"background_color\":\"#9147FF\",\"is_user_input_required\":true," +
        "\"max_per_stream_setting\":{\"is_enabled\":true,\"max_per_stream\":3}," +
        "\"max_per_user_per_stream_setting\":{\"is_enabled\":false,\"max_per_user_per_stream\":0}," +
        "\"global_cooldown_setting\":{\"is_enabled\":true,\"global_cooldown_seconds\":120}," +
        "\"should_redemptions_skip_request_queue\":false}]}";

    [Fact]
    public void Parses_every_setting_needed_to_copy_a_reward()
    {
        var reward = Assert.Single(TwitchClient.ParseRewards(FullRewardJson));

        Assert.Equal("r1", reward.Id);
        Assert.Equal(500, reward.Cost);
        Assert.Equal("Название трека", reward.Prompt);
        Assert.Equal("#9147FF", reward.BackgroundColor);
        Assert.True(reward.IsUserInputRequired);
        Assert.Equal(3, reward.MaxPerStream);
        Assert.Null(reward.MaxPerUserPerStream);
        Assert.Equal(120, reward.GlobalCooldownSeconds);
        Assert.False(reward.SkipRequestQueue);
    }

    [Fact]
    public async Task Lists_only_the_rewards_this_application_created()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"id\":\"m1\",\"title\":\"A\",\"cost\":1},{\"id\":\"m2\",\"title\":\"B\",\"cost\":2}]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var ids = await client.GetManageableRewardIdsAsync(CancellationToken.None);

        Assert.Equal(new[] { "m1", "m2" }, ids.OrderBy(i => i).ToArray());
        Assert.Contains("only_manageable_rewards=true", Assert.Single(da.Requests));
        Assert.Contains("broadcaster_id=777", da.Requests[0]);
    }

    [Fact]
    public async Task Creates_a_copy_with_every_setting_and_returns_its_id()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            Assert.Equal("POST", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.StartsWith("application/json", req.ContentType);
            return (200, "{\"data\":[{\"id\":\"new-1\",\"title\":\"x\"}]}");
        };
        var template = TwitchClient.ParseRewards(FullRewardJson)[0];
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.CreateRewardAsync(template, "Заказать трек​", CancellationToken.None);

        Assert.Equal(RewardCreateOutcome.Created, result.Outcome);
        Assert.Equal("new-1", result.Id);
        var request = Assert.Single(da.Requests);
        Assert.Contains("POST /helix/channel_points/custom_rewards?broadcaster_id=777", request);
        var json = request[(request.IndexOf("body=", StringComparison.Ordinal) + 5)..];
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Заказать трек​", root.GetProperty("title").GetString());
        Assert.Equal(500, root.GetProperty("cost").GetInt64());
        Assert.Equal("Название трека", root.GetProperty("prompt").GetString());
        Assert.Equal("#9147FF", root.GetProperty("background_color").GetString());
        Assert.True(root.GetProperty("is_enabled").GetBoolean());
        Assert.True(root.GetProperty("is_user_input_required").GetBoolean());
        Assert.True(root.GetProperty("is_max_per_stream_enabled").GetBoolean());
        Assert.Equal(3, root.GetProperty("max_per_stream").GetInt32());
        Assert.False(root.TryGetProperty("is_max_per_user_per_stream_enabled", out _));
        Assert.True(root.GetProperty("is_global_cooldown_enabled").GetBoolean());
        Assert.Equal(120, root.GetProperty("global_cooldown_seconds").GetInt32());
        Assert.False(root.GetProperty("should_redemptions_skip_request_queue").GetBoolean());
    }

    [Fact]
    public async Task A_duplicate_title_and_the_channel_limit_are_told_apart_from_other_failures()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var message = "CREATE_CUSTOM_REWARD_DUPLICATE_REWARD";
        da.Handler = (_, _) => (400, "{\"error\":\"Bad Request\",\"status\":400,\"message\":\"" + message + "\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var template = new RewardInfo("r", "T", 10, true);

        Assert.Equal(RewardCreateOutcome.TitleTaken, (await client.CreateRewardAsync(template, "T", CancellationToken.None)).Outcome);
        message = "CREATE_CUSTOM_REWARD_TOO_MANY_REWARDS";
        Assert.Equal(RewardCreateOutcome.LimitReached, (await client.CreateRewardAsync(template, "T", CancellationToken.None)).Outcome);
        message = "something else";
        var other = await client.CreateRewardAsync(template, "T", CancellationToken.None);
        Assert.Equal(RewardCreateOutcome.Failed, other.Outcome);
        Assert.Contains("something else", other.Message);
    }

    [Fact]
    public async Task Without_the_manage_right_no_request_is_made_and_a_new_login_is_requested()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "channel:read:redemptions"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var template = new RewardInfo("r", "T", 10, true);

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.CreateRewardAsync(template, "T", CancellationToken.None));
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetManageableRewardIdsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.UpdateRedemptionAsync("r", "x", RedemptionDecision.Fulfilled, CancellationToken.None));

        Assert.Empty(da.Requests);
    }

    [Fact]
    public async Task Accepting_sends_a_patch_with_fulfilled_and_refusing_sends_canceled()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            Assert.Equal("PATCH", req.HttpMethod);
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.Equal("twitch-client", req.Headers["Client-Id"]);
            return (200, "{\"data\":[{}]}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var accepted = await client.UpdateRedemptionAsync("reward-9", "redemption-5", RedemptionDecision.Fulfilled, CancellationToken.None);
        var refused = await client.UpdateRedemptionAsync("reward-9", "redemption-5", RedemptionDecision.Canceled, CancellationToken.None);

        Assert.Equal(RedemptionUpdateOutcome.Done, accepted.Outcome);
        Assert.Equal(RedemptionUpdateOutcome.Done, refused.Outcome);
        Assert.Contains("PATCH /helix/channel_points/custom_rewards/redemptions?id=redemption-5&broadcaster_id=777&reward_id=reward-9", da.Requests[0]);
        Assert.Contains("\"status\":\"FULFILLED\"", da.Requests[0]);
        Assert.Contains("\"status\":\"CANCELED\"", da.Requests[1]);
    }

    [Theory]
    [InlineData(400, RedemptionUpdateOutcome.AlreadyProcessed)]
    [InlineData(403, RedemptionUpdateOutcome.NotAllowed)]
    [InlineData(404, RedemptionUpdateOutcome.NotFound)]
    [InlineData(500, RedemptionUpdateOutcome.Failed)]
    public async Task Twitch_answers_are_mapped_to_outcomes(int status, RedemptionUpdateOutcome expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"explanation\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.UpdateRedemptionAsync("r", "x", RedemptionDecision.Fulfilled, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once_and_repeats_the_call()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000}");
            return req.Headers["Authorization"] == "Bearer tw-access-2" ? (200, "{\"data\":[{}]}") : (401, "{}");
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.UpdateRedemptionAsync("r", "x", RedemptionDecision.Fulfilled, CancellationToken.None);

        Assert.Equal(RedemptionUpdateOutcome.Done, result.Outcome);
        Assert.Equal(3, da.Requests.Count);
    }

    [Fact]
    public void The_device_flow_asks_for_the_manage_right_too_and_the_setting_reflects_it()
    {
        Assert.Contains("channel:manage:redemptions", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = "a channel:manage:redemptions" }.HasManageScope);
        Assert.False(new AppSettings { TwitchScopes = "channel:read:redemptions" }.HasManageScope);
    }
}

public class RewardSyncTests
{
    private static RewardInfo R(string id, string title, long cost = 100, bool enabled = true) => new(id, title, cost, enabled)
    {
        Prompt = "p-" + id,
        BackgroundColor = "#112233",
        IsUserInputRequired = true,
        MaxPerStream = 2,
        GlobalCooldownSeconds = 60,
    };

    private static readonly IReadOnlyDictionary<string, string> NoCopies = new Dictionary<string, string>();

    [Fact]
    public void Titles_get_an_invisible_mark_or_a_visible_suffix_within_the_45_character_limit()
    {
        Assert.Equal("Заказать трек​", RewardSync.InvisibleTitle("Заказать трек"));
        Assert.Equal(45, RewardSync.InvisibleTitle(new string('a', 44))!.Length);
        Assert.Null(RewardSync.InvisibleTitle(new string('a', 45)));

        Assert.Equal("Трек (копия)", RewardSync.VisibleTitle("Трек"));
        var longTitle = RewardSync.VisibleTitle(new string('b', 45));
        Assert.True(longTitle.Length <= 45);
        Assert.EndsWith(" (копия)", longTitle);
    }

    [Fact]
    public async Task Every_reward_gets_a_copy_with_the_same_settings_and_an_invisible_title_mark()
    {
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Заказать трек", 500));
        api.Rewards.Add(R("b", "Эмодзи", 2500, enabled: false));

        var result = await RewardSync.RunAsync(api, NoCopies, CancellationToken.None);

        Assert.Equal(2, result.CreatedInvisible);
        Assert.Equal(0, result.CreatedVisible);
        Assert.Equal(2, api.Created.Count);
        Assert.Equal("Заказать трек​", api.Created[0].Title);
        Assert.Equal(500, api.Created[0].Template.Cost);
        Assert.Equal("p-a", api.Created[0].Template.Prompt);
        Assert.Equal(2, api.Created[0].Template.MaxPerStream);
        Assert.True(api.Created[1].Template.IsEnabled == false, "a reward that was switched off stays off in its copy");
        Assert.Equal(new[] { "a", "b" }, result.Copies.Select(c => c.OriginalId).OrderBy(x => x).ToArray());
        Assert.All(result.Copies, c => Assert.Contains(c.CopyId, result.ManagedIds));
    }

    [Fact]
    public async Task When_twitch_refuses_the_invisible_title_the_visible_suffix_is_used()
    {
        var api = new FakeRewardApi
        {
            OnCreate = (_, title) => title.EndsWith('​')
                ? new RewardCreateResult(RewardCreateOutcome.TitleTaken, null, "DUPLICATE")
                : new RewardCreateResult(RewardCreateOutcome.Created, "copy-x", ""),
        };
        api.Rewards.Add(R("a", "Трек"));

        var result = await RewardSync.RunAsync(api, NoCopies, CancellationToken.None);

        Assert.Equal(0, result.CreatedInvisible);
        Assert.Equal(1, result.CreatedVisible);
        Assert.Equal(new[] { "Трек​", "Трек (копия)" }, api.Created.Select(c => c.Title).ToArray());
        Assert.Equal("copy-x", Assert.Single(result.Copies).CopyId);
    }

    [Fact]
    public async Task A_title_that_already_uses_all_45_characters_goes_straight_to_the_visible_suffix()
    {
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", new string('x', 45)));

        var result = await RewardSync.RunAsync(api, NoCopies, CancellationToken.None);

        Assert.Equal(1, result.CreatedVisible);
        var title = Assert.Single(api.Created).Title;
        Assert.True(title.Length <= 45);
        Assert.EndsWith(" (копия)", title);
    }

    [Fact]
    public async Task Rewards_that_already_have_a_copy_are_not_copied_again_and_managed_rewards_are_never_copied()
    {
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Трек"));
        api.Rewards.Add(R("a-copy", "Трек​"));
        api.Managed.Add("a-copy");
        api.Rewards.Add(R("b", "Эмодзи"));
        api.Rewards.Add(R("b-copy", "Эмодзи (копия)"));
        api.Managed.Add("b-copy");
        api.Rewards.Add(R("c", "Вода"));
        var known = new Dictionary<string, string> { ["c"] = "c-copy" };
        api.Rewards.Add(R("c-copy", "Вода", enabled: true));
        api.Managed.Add("c-copy");

        var result = await RewardSync.RunAsync(api, known, CancellationToken.None);

        Assert.Empty(api.Created);
        Assert.Equal(3, result.AlreadyCopied);
        Assert.Equal(0, result.Created);
    }

    [Fact]
    public async Task The_limit_of_50_rewards_per_channel_stops_the_copying_and_is_reported()
    {
        var api = new FakeRewardApi();
        for (var i = 0; i < 28; i++) api.Rewards.Add(R("r" + i, "Награда " + i));

        var result = await RewardSync.RunAsync(api, NoCopies, CancellationToken.None);

        Assert.Equal(22, result.Created);
        Assert.Equal(6, result.SkippedByLimit);
        Assert.Equal(50, api.Rewards.Count);
    }

    [Fact]
    public async Task A_limit_error_from_twitch_skips_the_rest_and_other_errors_do_not_stop_the_run()
    {
        var calls = 0;
        var api = new FakeRewardApi
        {
            OnCreate = (template, title) =>
            {
                calls++;
                return template.Id switch
                {
                    "a" => new RewardCreateResult(RewardCreateOutcome.Failed, null, "boom"),
                    "b" => new RewardCreateResult(RewardCreateOutcome.LimitReached, null, "TOO_MANY"),
                    _ => new RewardCreateResult(RewardCreateOutcome.Created, "ok-" + template.Id, ""),
                };
            },
        };
        api.Rewards.Add(R("a", "Раз"));
        api.Rewards.Add(R("b", "Два"));
        api.Rewards.Add(R("c", "Три"));

        var result = await RewardSync.RunAsync(api, NoCopies, CancellationToken.None);

        Assert.Equal("Раз: boom", Assert.Single(result.Failures));
        Assert.Equal(2, result.SkippedByLimit);
        Assert.Equal(0, result.Created);
        Assert.Equal(2, calls);
    }
}

public class RedemptionDecisionTests
{
    private static Redemption Pending(bool canManage = true) => new()
    {
        Key = "red-1", RewardId = "rw-1", DisplayName = "Anna", RewardTitle = "Трек", Cost = 100, CanManage = canManage,
    };

    [Fact]
    public async Task Accepting_marks_the_card_accepted_seen_and_done()
    {
        var api = new FakeRewardApi();
        var redemption = Pending();

        var result = await RedemptionDecisions.DecideAsync(api, redemption, RedemptionDecision.Fulfilled, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Resolved);
        Assert.Equal(RedemptionStatus.Accepted, redemption.Status);
        Assert.True(redemption.Seen);
        Assert.True(redemption.Done);
        Assert.Equal(("rw-1", "red-1", RedemptionDecision.Fulfilled), Assert.Single(api.Updates));
        Assert.False(redemption.ShowActions);
        Assert.Equal("Принято в Twitch", redemption.StatusText);
    }

    [Fact]
    public async Task Refusing_marks_the_card_rejected_but_not_done()
    {
        var api = new FakeRewardApi();
        var redemption = Pending();

        var result = await RedemptionDecisions.DecideAsync(api, redemption, RedemptionDecision.Canceled, CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.Resolved);
        Assert.Equal(RedemptionStatus.Rejected, redemption.Status);
        Assert.True(redemption.Seen);
        Assert.False(redemption.Done);
        Assert.Contains("баллы возвращены", redemption.StatusText);
    }

    [Fact]
    public async Task An_unmanaged_or_already_decided_redemption_is_never_sent_to_twitch()
    {
        var api = new FakeRewardApi();
        var unmanaged = Pending(canManage: false);
        var decided = Pending();
        decided.Status = RedemptionStatus.Accepted;

        var unmanagedResult = await RedemptionDecisions.DecideAsync(api, unmanaged, RedemptionDecision.Fulfilled, CancellationToken.None);
        var decidedResult = await RedemptionDecisions.DecideAsync(api, decided, RedemptionDecision.Canceled, CancellationToken.None);
        Assert.False(unmanagedResult.Success);
        Assert.False(unmanagedResult.Resolved);
        Assert.False(decidedResult.Success);
        Assert.False(decidedResult.Resolved);

        Assert.Empty(api.Updates);
        Assert.Equal(RedemptionStatus.Accepted, decided.Status);
    }

    [Fact]
    public async Task Twitch_refusals_are_reflected_without_pretending_success()
    {
        var api = new FakeRewardApi();

        var forbidden = Pending();
        api.UpdateResult = new RedemptionUpdateResult(RedemptionUpdateOutcome.NotAllowed, "x");
        var r1 = await RedemptionDecisions.DecideAsync(api, forbidden, RedemptionDecision.Fulfilled, CancellationToken.None);
        Assert.False(r1.Success);
        Assert.False(r1.Resolved);
        Assert.False(forbidden.CanManage);
        Assert.Equal(RedemptionStatus.Pending, forbidden.Status);

        var processed = Pending();
        api.UpdateResult = new RedemptionUpdateResult(RedemptionUpdateOutcome.AlreadyProcessed, "x");
        var r2 = await RedemptionDecisions.DecideAsync(api, processed, RedemptionDecision.Canceled, CancellationToken.None);
        Assert.False(r2.Success);
        Assert.True(r2.Resolved);
        Assert.Equal(RedemptionStatus.Processed, processed.Status);

        var failed = Pending();
        api.UpdateResult = new RedemptionUpdateResult(RedemptionUpdateOutcome.Failed, "HTTP 500");
        var r3 = await RedemptionDecisions.DecideAsync(api, failed, RedemptionDecision.Fulfilled, CancellationToken.None);
        Assert.False(r3.Success);
        Assert.False(r3.Resolved);
        Assert.Contains("HTTP 500", r3.Message);
        Assert.Equal(RedemptionStatus.Pending, failed.Status);
        Assert.True(failed.CanManage);
    }

    [Fact]
    public async Task The_same_result_flag_is_set_for_a_NotFound_order_so_a_vanished_order_leaves_the_list_too()
    {
        var api = new FakeRewardApi { UpdateResult = new RedemptionUpdateResult(RedemptionUpdateOutcome.NotFound, "gone") };
        var redemption = Pending();

        var result = await RedemptionDecisions.DecideAsync(api, redemption, RedemptionDecision.Fulfilled, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Resolved);
        Assert.Equal(RedemptionStatus.Processed, redemption.Status);
    }

    [Fact]
    public void Delete_is_offered_only_when_there_is_no_accept_or_refuse()
    {
        var pending = Pending();
        Assert.True(pending.ShowActions);
        Assert.False(pending.ShowDelete);

        pending.ConfirmingReject = true;
        Assert.False(pending.ShowDelete);

        Assert.True(Pending(canManage: false).ShowDelete);

        foreach (var status in new[] { RedemptionStatus.Accepted, RedemptionStatus.Rejected, RedemptionStatus.Processed })
        {
            var decided = Pending();
            decided.Status = status;
            Assert.True(decided.ShowDelete, status.ToString());
        }
    }

    [Fact]
    public void Delete_follows_the_card_state_changes()
    {
        var redemption = Pending();
        var raised = new List<string?>();
        redemption.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        redemption.CanManage = false;

        Assert.Contains(nameof(Redemption.ShowDelete), raised);
        Assert.True(redemption.ShowDelete);
    }

    [Fact]
    public void The_card_shows_actions_then_a_confirmation_and_hides_both_once_decided()
    {
        var redemption = Pending();
        var raised = new List<string?>();
        redemption.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(redemption.ShowActions);
        Assert.False(redemption.ShowConfirm);

        redemption.ConfirmingReject = true;
        Assert.False(redemption.ShowActions);
        Assert.True(redemption.ShowConfirm);

        redemption.Status = RedemptionStatus.Rejected;
        Assert.False(redemption.ShowActions);
        Assert.False(redemption.ShowConfirm);
        Assert.True(redemption.HasStatusText);
        Assert.Contains(nameof(Redemption.ShowActions), raised);
        Assert.Contains(nameof(Redemption.ShowConfirm), raised);
        Assert.Contains(nameof(Redemption.StatusText), raised);
    }

    [Fact]
    public void The_status_and_the_manage_flag_survive_a_restart_and_old_files_load_as_pending()
    {
        using var dir = new TempDir();
        var path = dir.File("redemptions.json");
        var store = new RedemptionStore(path);
        store.AddRange(new[] { Pending(), new Redemption { Key = "red-2", RewardId = "rw-2", AtUtc = DateTime.UtcNow, CanManage = true } });
        store.Items.First(r => r.Key == "red-1").Status = RedemptionStatus.Rejected;

        var reloaded = new RedemptionStore(path);

        var first = reloaded.Items.First(r => r.Key == "red-1");
        Assert.Equal(RedemptionStatus.Rejected, first.Status);
        Assert.True(first.CanManage);
        Assert.Contains("\"Rejected\"", File.ReadAllText(path));

        File.WriteAllText(dir.File("old.json"), "[{\"Key\":\"o\",\"Login\":\"x\",\"DisplayName\":\"X\",\"RewardTitle\":\"T\",\"Cost\":5,\"UserInput\":\"\",\"AtUtc\":\"2026-01-01T00:00:00Z\",\"Seen\":false,\"Done\":false}]");
        var old = Assert.Single(new RedemptionStore(dir.File("old.json")).Items);
        Assert.Equal(RedemptionStatus.Pending, old.Status);
        Assert.False(old.CanManage);
        Assert.False(old.ShowActions);
    }

    [Fact]
    public void A_redemption_that_arrives_already_fulfilled_has_nothing_to_accept()
    {
        var fulfilled = "{\"id\":\"r-1\",\"user_login\":\"a\",\"user_name\":\"A\",\"status\":\"fulfilled\",\"reward\":{\"id\":\"rw\",\"title\":\"T\",\"cost\":1}}";
        var unfulfilled = fulfilled.Replace("fulfilled", "unfulfilled");

        var auto = EventSubParser.Parse(MockEventSubServer.Notification("m1", EventSubParser.RedemptionType, fulfilled))!.Redemption!;
        var queued = EventSubParser.Parse(MockEventSubServer.Notification("m2", EventSubParser.RedemptionType, unfulfilled))!.Redemption!;

        Assert.Equal(RedemptionStatus.Processed, auto.Status);
        Assert.Equal(RedemptionStatus.Pending, queued.Status);
    }
}
