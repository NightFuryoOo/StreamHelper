using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal sealed class FakeFollowerSource : IFollowerSource
{
    private readonly Dictionary<string, FollowerPage> _pages;

    public FakeFollowerSource(Dictionary<string, FollowerPage> pages) => _pages = pages;

    public List<string?> Requested { get; } = new();

    public Task<FollowerPage> GetFollowersAsync(string? cursor, CancellationToken ct)
    {
        Requested.Add(cursor);
        return Task.FromResult(_pages.TryGetValue(cursor ?? "", out var page) ? page : new FollowerPage(Array.Empty<Follower>(), null));
    }
}

internal static class MakeFollower
{
    public static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    public static Follower F(string userId, int secondsAfterT0) =>
        new() { UserId = userId, Login = userId, DisplayName = userId.ToUpperInvariant(), FollowedAtUtc = T0.AddSeconds(secondsAfterT0) };

    public static FollowerPage Page(string? next, params Follower[] items) => new(items, next);

    public static string PageJson(string? cursor, params (string id, string name, string at)[] items)
    {
        var data = string.Join(",", items.Select(i =>
            $"{{\"user_id\":\"{i.id}\",\"user_login\":\"{i.name.ToLowerInvariant()}\",\"user_name\":\"{i.name}\",\"followed_at\":\"{i.at}\"}}"));
        var pagination = cursor == null ? "{}" : $"{{\"cursor\":\"{cursor}\"}}";
        return $"{{\"total\":{items.Length},\"data\":[{data}],\"pagination\":{pagination}}}";
    }

    public static SettingsStore Settings(TempDir dir)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.TwitchClientId = "twitch-client";
        store.Current.TwitchAccessToken = "tw-access-1";
        store.Current.TwitchRefreshToken = "tw-refresh-1";
        store.Current.TwitchAccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
        store.Current.TwitchUserId = "777";
        store.Current.TwitchLogin = "streamer";
        return store;
    }
}

public class TwitchParsingTests
{
    [Fact]
    public void Parses_the_documented_follower_page()
    {
        var json = MakeFollower.PageJson("abc123", ("11", "Alice", "2026-10-05T12:00:05Z"), ("22", "Bob", "2026-10-05T11:00:00Z"));

        var page = TwitchClient.ParseFollowers(json);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("11", page.Items[0].UserId);
        Assert.Equal("Alice", page.Items[0].DisplayName);
        Assert.Equal("alice", page.Items[0].Login);
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 5, DateTimeKind.Utc), page.Items[0].FollowedAtUtc);
        Assert.Equal(DateTimeKind.Utc, page.Items[0].FollowedAtUtc.Kind);
        Assert.Equal("abc123", page.Cursor);
    }

    [Fact]
    public void An_empty_pagination_object_means_no_next_page_and_unknown_shapes_are_skipped()
    {
        var json = "{\"data\":[{\"user_login\":\"no_id\"},{\"user_id\":\"5\",\"user_login\":\"x\",\"user_name\":\"X\",\"followed_at\":\"bad\"}],\"pagination\":{}}";

        var page = TwitchClient.ParseFollowers(json);

        var follower = Assert.Single(page.Items);
        Assert.Equal("5", follower.UserId);
        Assert.Equal(default, follower.FollowedAtUtc);
        Assert.Null(page.Cursor);
    }

    [Fact]
    public void Parses_the_device_code_response()
    {
        var json = "{\"device_code\":\"dc\",\"expires_in\":1800,\"interval\":5,\"user_code\":\"ABCDEFGH\",\"verification_uri\":\"https://www.twitch.tv/activate?device-code=ABCDEFGH\"}";

        var info = TwitchClient.ParseDeviceCode(json);

        Assert.Equal("dc", info.DeviceCode);
        Assert.Equal("ABCDEFGH", info.UserCode);
        Assert.Equal(1800, info.ExpiresInSeconds);
        Assert.Equal(5, info.IntervalSeconds);
        Assert.Contains("ABCDEFGH", info.VerificationUri);
    }

    [Fact]
    public void A_follower_key_is_stable_and_differs_for_a_refollow()
    {
        var first = MakeFollower.F("1", 0);
        var again = MakeFollower.F("1", 0);
        var refollow = MakeFollower.F("1", 90);

        Assert.Equal(first.Key, again.Key);
        Assert.NotEqual(first.Key, refollow.Key);
    }
}

public class TwitchClientTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Device_flow_waits_through_pending_then_stores_tokens_and_the_channel()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var polls = 0;
        da.Handler = (req, body) =>
        {
            switch (req.Url!.AbsolutePath)
            {
                case "/oauth2/device":
                    Assert.Contains("client_id=twitch-client", body);
                    Assert.Contains("scopes=moderator%3Aread%3Afollowers", body);
                    return (200, "{\"device_code\":\"dc\",\"expires_in\":600,\"interval\":0,\"user_code\":\"CODE1234\",\"verification_uri\":\"https://x/activate\"}");
                case "/oauth2/token":
                    Assert.Contains("device_code=dc", body);
                    Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", body);
                    return ++polls < 3
                        ? (400, "{\"status\":400,\"message\":\"authorization_pending\"}")
                        : (200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":14000,\"scope\":[\"moderator:read:followers\"],\"token_type\":\"bearer\"}");
                case "/helix/users":
                    Assert.Equal("Bearer AT", req.Headers["Authorization"]);
                    Assert.Equal("twitch-client", req.Headers["Client-Id"]);
                    return (200, "{\"data\":[{\"id\":\"4242\",\"login\":\"mystream\"}]}");
                default:
                    return (404, "");
            }
        };
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.TwitchClientId = "twitch-client";
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var info = await client.StartDeviceFlowAsync(CancellationToken.None);
        Assert.Equal("CODE1234", info.UserCode);
        await client.CompleteDeviceFlowAsync(info, CancellationToken.None);

        Assert.Equal(3, polls);
        Assert.Equal("AT", settings.Current.TwitchAccessToken);
        Assert.Equal("RT", settings.Current.TwitchRefreshToken);
        Assert.Equal("4242", settings.Current.TwitchUserId);
        Assert.Equal("mystream", settings.Current.TwitchLogin);
        Assert.True(settings.Current.HasTwitchTokens);
        Assert.DoesNotContain("\"AT\"", File.ReadAllText(dir.File("settings.json")));
    }

    [Fact]
    public async Task Device_flow_surfaces_a_denial_instead_of_waiting_forever()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (400, "{\"status\":400,\"message\":\"access_denied\"}");
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.TwitchClientId = "twitch-client";
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteDeviceFlowAsync(new DeviceCodeInfo("dc", "X", "u", 600, 0), CancellationToken.None));

        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task Reconnecting_the_same_channel_keeps_the_follower_baseline_but_another_channel_resets_it()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var userId = "777";
        da.Handler = (req, _) => req.Url!.AbsolutePath switch
        {
            "/oauth2/token" => (200, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":14000}"),
            "/helix/users" => (200, $"{{\"data\":[{{\"id\":\"{userId}\",\"login\":\"streamer\"}}]}}"),
            _ => (404, ""),
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.FollowersBaselined = true;
        settings.Current.LastFollowerAtUtc = MakeFollower.T0;
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var info = new DeviceCodeInfo("dc", "X", "u", 600, 0);

        await client.CompleteDeviceFlowAsync(info, CancellationToken.None);
        Assert.True(settings.Current.FollowersBaselined);
        Assert.Equal(MakeFollower.T0, settings.Current.LastFollowerAtUtc);

        userId = "888";
        await client.CompleteDeviceFlowAsync(info, CancellationToken.None);
        Assert.False(settings.Current.FollowersBaselined);
        Assert.Equal(default, settings.Current.LastFollowerAtUtc);
    }

    [Fact]
    public async Task Requests_followers_for_the_channel_with_the_right_headers_and_cursor()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            Assert.Equal("Bearer tw-access-1", req.Headers["Authorization"]);
            Assert.Equal("twitch-client", req.Headers["Client-Id"]);
            return (200, MakeFollower.PageJson("next", ("1", "Alice", "2026-10-05T12:00:00Z")));
        };
        var client = new TwitchClient(MakeFollower.Settings(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var page = await client.GetFollowersAsync("cur sor", CancellationToken.None);

        Assert.Equal("next", page.Cursor);
        var request = Assert.Single(da.Requests);
        Assert.Contains("broadcaster_id=777", request);
        Assert.Contains("first=100", request);
        Assert.Contains("after=cur%20sor", request);
    }

    [Fact]
    public async Task Refreshes_an_expired_token_without_a_client_secret_and_stores_the_new_one_time_refresh_token()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
            {
                Assert.Contains("grant_type=refresh_token", body);
                Assert.Contains("refresh_token=tw-refresh-1", body);
                Assert.Contains("client_id=twitch-client", body);
                Assert.DoesNotContain("client_secret", body);
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000}");
            }
            Assert.Equal("Bearer tw-access-2", req.Headers["Authorization"]);
            return (200, MakeFollower.PageJson(null));
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(-10);
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.GetFollowersAsync(null, CancellationToken.None);

        Assert.Equal("tw-refresh-2", settings.Current.TwitchRefreshToken);
        Assert.Equal("tw-access-2", settings.Current.TwitchAccessToken);
    }

    [Fact]
    public async Task A_401_triggers_one_refresh_and_a_retry()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) =>
        {
            if (req.Url!.AbsolutePath == "/oauth2/token")
                return (200, "{\"access_token\":\"tw-access-2\",\"refresh_token\":\"tw-refresh-2\",\"expires_in\":14000}");
            return req.Headers["Authorization"] == "Bearer tw-access-2"
                ? (200, MakeFollower.PageJson(null, ("1", "Alice", "2026-10-05T12:00:00Z")))
                : (401, "{\"status\":401,\"message\":\"Invalid OAuth token\"}");
        };
        var client = new TwitchClient(MakeFollower.Settings(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var page = await client.GetFollowersAsync(null, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(3, da.Requests.Count);
    }

    [Fact]
    public async Task A_rejected_refresh_token_clears_the_login_and_asks_to_connect_again()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => req.Url!.AbsolutePath == "/oauth2/token"
            ? (400, "{\"status\":400,\"message\":\"Invalid refresh token\"}")
            : (401, "{}");
        var settings = MakeFollower.Settings(dir);
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetFollowersAsync(null, CancellationToken.None));

        Assert.Equal("", settings.Current.TwitchRefreshToken);
        Assert.Equal("", settings.Current.TwitchAccessToken);
    }

    [Fact]
    public async Task Missing_rights_are_reported_as_needing_a_new_login_and_rate_limits_as_plain_errors()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var status = 403;
        da.Handler = (_, _) => (status, "{}");
        var client = new TwitchClient(MakeFollower.Settings(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetFollowersAsync(null, CancellationToken.None));

        status = 429;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetFollowersAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task Without_tokens_it_asks_to_connect_and_makes_no_request()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.TwitchClientId = "twitch-client";
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetFollowersAsync(null, CancellationToken.None));

        Assert.Empty(da.Requests);
    }
}

public class FollowerSyncTests
{
    private static readonly string[] None = Array.Empty<string>();

    [Fact]
    public async Task First_run_only_remembers_the_newest_timestamp_and_imports_nothing()
    {
        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("c", 30), MakeFollower.F("b", 20), MakeFollower.F("a", 10)) });

        var result = await FollowerSync.FetchNewAsync(source, default, None, baselined: false, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(MakeFollower.T0.AddSeconds(30), result.LastFollowerAtUtc);
        Assert.Equal(new[] { "c" }, result.LastUserIds);
        Assert.True(result.Baselined);
    }

    [Fact]
    public async Task First_run_with_no_followers_is_baselined_and_then_everyone_is_new()
    {
        var empty = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null) });
        var first = await FollowerSync.FetchNewAsync(empty, default, None, baselined: false, CancellationToken.None);
        Assert.True(first.Baselined);
        Assert.Equal(default, first.LastFollowerAtUtc);

        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("a", 10)) });
        var second = await FollowerSync.FetchNewAsync(source, first.LastFollowerAtUtc, first.LastUserIds, true, CancellationToken.None);
        Assert.Equal(new[] { "a" }, second.Items.Select(f => f.UserId).ToArray());
    }

    [Fact]
    public async Task Returns_only_newer_followers_oldest_first()
    {
        var source = new FakeFollowerSource(new()
        {
            [""] = MakeFollower.Page(null, MakeFollower.F("e", 50), MakeFollower.F("d", 40), MakeFollower.F("c", 30), MakeFollower.F("b", 20)),
        });

        var result = await FollowerSync.FetchNewAsync(source, MakeFollower.T0.AddSeconds(30), new[] { "c" }, true, CancellationToken.None);

        Assert.Equal(new[] { "d", "e" }, result.Items.Select(f => f.UserId).ToArray());
        Assert.Equal(MakeFollower.T0.AddSeconds(50), result.LastFollowerAtUtc);
        Assert.Equal(new[] { "e" }, result.LastUserIds);
    }

    [Fact]
    public async Task A_second_follower_in_the_same_second_as_the_last_one_is_not_missed_and_the_first_is_not_repeated()
    {
        var at = MakeFollower.T0.AddSeconds(30);
        var source = new FakeFollowerSource(new()
        {
            [""] = MakeFollower.Page(null, MakeFollower.F("x2", 30), MakeFollower.F("x1", 30), MakeFollower.F("a", 10)),
        });

        var result = await FollowerSync.FetchNewAsync(source, at, new[] { "x1" }, true, CancellationToken.None);

        Assert.Equal(new[] { "x2" }, result.Items.Select(f => f.UserId).ToArray());
        Assert.Equal(at, result.LastFollowerAtUtc);
        Assert.Equal(new[] { "x1", "x2" }, result.LastUserIds.OrderBy(x => x).ToArray());

        var again = await FollowerSync.FetchNewAsync(source, result.LastFollowerAtUtc, result.LastUserIds, true, CancellationToken.None);
        Assert.Empty(again.Items);
    }

    [Fact]
    public async Task Nothing_new_changes_nothing()
    {
        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("b", 20), MakeFollower.F("a", 10)) });

        var result = await FollowerSync.FetchNewAsync(source, MakeFollower.T0.AddSeconds(20), new[] { "b" }, true, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(MakeFollower.T0.AddSeconds(20), result.LastFollowerAtUtc);
        Assert.Equal(new[] { "b" }, result.LastUserIds);
    }

    [Fact]
    public async Task Walks_to_the_next_page_while_everything_is_new_and_stops_at_the_known_boundary()
    {
        var source = new FakeFollowerSource(new()
        {
            [""] = MakeFollower.Page("p2", MakeFollower.F("f", 60), MakeFollower.F("e", 50)),
            ["p2"] = MakeFollower.Page("p3", MakeFollower.F("d", 40), MakeFollower.F("c", 30), MakeFollower.F("b", 20)),
            ["p3"] = MakeFollower.Page(null, MakeFollower.F("a", 10)),
        });

        var result = await FollowerSync.FetchNewAsync(source, MakeFollower.T0.AddSeconds(30), new[] { "c" }, true, CancellationToken.None);

        Assert.Equal(new[] { "d", "e", "f" }, result.Items.Select(f => f.UserId).ToArray());
        Assert.Equal(new string?[] { null, "p2" }, source.Requested.ToArray());
    }

    [Fact]
    public async Task Keeps_reading_past_a_page_that_ends_exactly_at_the_last_timestamp_because_ties_may_continue()
    {
        var source = new FakeFollowerSource(new()
        {
            [""] = MakeFollower.Page("p2", MakeFollower.F("e", 50), MakeFollower.F("d", 40)),
            ["p2"] = MakeFollower.Page("p3", MakeFollower.F("c", 30)),
            ["p3"] = MakeFollower.Page(null, MakeFollower.F("c2", 30), MakeFollower.F("b", 20)),
        });

        var result = await FollowerSync.FetchNewAsync(source, MakeFollower.T0.AddSeconds(30), new[] { "c" }, true, CancellationToken.None);

        Assert.Equal(new[] { "c2", "d", "e" }, result.Items.Select(f => f.UserId).ToArray());
        Assert.Equal(new string?[] { null, "p2", "p3" }, source.Requested.ToArray());
    }

    [Fact]
    public async Task A_user_who_unfollows_and_follows_again_shows_up_as_new()
    {
        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("a", 90), MakeFollower.F("b", 20)) });

        var result = await FollowerSync.FetchNewAsync(source, MakeFollower.T0.AddSeconds(20), new[] { "b" }, true, CancellationToken.None);

        Assert.Equal(new[] { "a" }, result.Items.Select(f => f.UserId).ToArray());
    }

    [Fact]
    public async Task Page_walk_is_capped()
    {
        var pages = new Dictionary<string, FollowerPage>();
        for (var i = 1; i <= 40; i++)
        {
            pages[i == 1 ? "" : "p" + i] = MakeFollower.Page(i == 40 ? null : "p" + (i + 1),
                MakeFollower.F("u" + i + "a", 100000 - i * 2), MakeFollower.F("u" + i + "b", 100000 - i * 2 - 1));
        }
        var source = new FakeFollowerSource(pages);

        await FollowerSync.FetchNewAsync(source, default, None, true, CancellationToken.None);

        Assert.Equal(FollowerSync.MaxPages, source.Requested.Count);
    }

    [Fact]
    public async Task Poller_baselines_then_delivers_new_followers_and_does_not_rewrite_settings_when_idle()
    {
        using var dir = new TempDir();
        var settings = MakeFollower.Settings(dir);
        var pages = new Dictionary<string, FollowerPage> { [""] = MakeFollower.Page(null, MakeFollower.F("b", 20), MakeFollower.F("a", 10)) };
        var source = new FakeFollowerSource(pages);
        var delivered = new List<string>();
        var poller = new FollowerPoller(settings, source, items =>
        {
            delivered.AddRange(items.Select(f => f.UserId));
            return Task.CompletedTask;
        });

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Empty(delivered);
        Assert.Equal(SyncState.Ok, poller.Status.State);
        Assert.Equal("Подключено · streamer", poller.Status.Message);

        pages[""] = MakeFollower.Page(null, MakeFollower.F("c", 30), MakeFollower.F("b", 20), MakeFollower.F("a", 10));
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "c" }, delivered);

        File.Delete(dir.File("settings.json"));
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "c" }, delivered);
        Assert.False(File.Exists(dir.File("settings.json")));
    }

    [Fact]
    public async Task Poller_does_not_advance_when_delivery_fails()
    {
        using var dir = new TempDir();
        var settings = MakeFollower.Settings(dir);
        settings.Current.FollowersBaselined = true;
        settings.Current.LastFollowerAtUtc = MakeFollower.T0.AddSeconds(10);
        settings.Current.LastFollowerUserIds = new List<string> { "a" };
        var source = new FakeFollowerSource(new() { [""] = MakeFollower.Page(null, MakeFollower.F("b", 20), MakeFollower.F("a", 10)) });
        var poller = new FollowerPoller(settings, source, _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollOnceAsync(CancellationToken.None));

        Assert.Equal(MakeFollower.T0.AddSeconds(10), settings.Current.LastFollowerAtUtc);
    }

    [Fact]
    public async Task Poller_reports_not_connected_without_a_login_and_makes_no_request()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        var source = new FakeFollowerSource(new());
        var poller = new FollowerPoller(settings, source, _ => Task.CompletedTask);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(SyncState.NotConnected, poller.Status.State);
        Assert.Empty(source.Requested);
    }
}

public class FollowerStoreTests
{
    [Fact]
    public void Adds_newest_first_dedupes_by_key_counts_unseen_and_persists()
    {
        using var dir = new TempDir();
        var path = dir.File("followers.json");
        var store = new FollowerStore(path);

        store.AddRange(new[] { MakeFollower.F("b", 20), MakeFollower.F("a", 10) });
        var added = store.AddRange(new[] { MakeFollower.F("b", 20), MakeFollower.F("c", 30) });

        Assert.Equal(new[] { "c", "b", "a" }, store.Items.Select(f => f.UserId).ToArray());
        Assert.Single(added);
        Assert.Equal(3, store.UnseenCount);

        store.Items[0].Seen = true;
        Assert.Equal(2, store.UnseenCount);

        var reloaded = new FollowerStore(path);
        Assert.Equal(new[] { "c", "b", "a" }, reloaded.Items.Select(f => f.UserId).ToArray());
        Assert.True(reloaded.Items[0].Seen);
        Assert.Equal(2, reloaded.UnseenCount);
        Assert.Equal("C", reloaded.Items[0].DisplayName);
    }

    [Fact]
    public void A_refollow_of_the_same_user_is_a_separate_entry()
    {
        using var dir = new TempDir();
        var store = new FollowerStore(dir.File("followers.json"));

        store.AddRange(new[] { MakeFollower.F("a", 10) });
        store.AddRange(new[] { MakeFollower.F("a", 500) });

        Assert.Equal(2, store.Items.Count);
        Assert.Equal(MakeFollower.T0.AddSeconds(500), store.Items[0].FollowedAtUtc);
    }

    [Fact]
    public void Remove_and_restore_put_the_follower_back_in_place()
    {
        using var dir = new TempDir();
        var path = dir.File("followers.json");
        var store = new FollowerStore(path);
        store.AddRange(new[] { MakeFollower.F("a", 10), MakeFollower.F("b", 20), MakeFollower.F("c", 30) });
        var victim = store.Items[1];

        var index = store.Remove(victim);
        Assert.Equal(new[] { "c", "a" }, new FollowerStore(path).Items.Select(f => f.UserId).ToArray());

        store.Restore(victim, index);
        Assert.Equal(new[] { "c", "b", "a" }, new FollowerStore(path).Items.Select(f => f.UserId).ToArray());
    }

    [Fact]
    public void Followers_and_donations_are_stored_independently()
    {
        using var dir = new TempDir();
        var donations = new DonationStore(dir.File("donations.json"));
        var followers = new FollowerStore(dir.File("followers.json"));

        donations.AddRange(new[] { Make.D(1) });
        followers.AddRange(new[] { MakeFollower.F("a", 10) });
        followers.Items[0].Seen = true;

        Assert.Equal(1, donations.UnseenCount);
        Assert.Equal(0, followers.UnseenCount);
        Assert.Single(new DonationStore(dir.File("donations.json")).Items);
        Assert.Single(new FollowerStore(dir.File("followers.json")).Items);
    }
}
