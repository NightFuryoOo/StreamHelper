using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class ParsingTests
{
    private const string DocsSample = """
        {"data":[{"id":30530030,"name":"donation","username":"Ivan","message_type":"text","message":"Hello!",
        "amount":500,"currency":"RUB","is_shown":1,"created_at":"2019-09-29 09:00:00","shown_at":null}],
        "links":{"first":"x","last":"x","prev":null,"next":null},
        "meta":{"current_page":1,"from":1,"last_page":1,"path":"x","per_page":30,"to":1,"total":1}}
        """;

    [Fact]
    public void Parses_the_documented_sample()
    {
        var page = DonationAlertsClient.ParseDonations(DocsSample);

        var item = Assert.Single(page.Items);
        Assert.Equal(30530030, item.Id);
        Assert.Equal("Ivan", item.Username);
        Assert.Equal("Hello!", item.Message);
        Assert.Equal(500m, item.Amount);
        Assert.Equal("RUB", item.Currency);
        Assert.Equal(new DateTime(2019, 9, 29, 9, 0, 0, DateTimeKind.Utc), item.CreatedAtUtc);
        Assert.False(page.HasNext);
        Assert.Equal(1, page.LastPage);
    }

    [Fact]
    public void Tolerates_string_amounts_null_fields_and_unknown_shapes()
    {
        var json = """
            {"data":[{"id":7,"username":null,"message":null,"amount":"12.5","currency":"USD","created_at":"garbage"},
                     {"no_id":true}],
             "links":{"next":"https://x/?page=2"},"meta":{"current_page":1,"last_page":4}}
            """;

        var page = DonationAlertsClient.ParseDonations(json);

        var item = Assert.Single(page.Items);
        Assert.Equal(12.5m, item.Amount);
        Assert.Equal("", item.Username);
        Assert.Equal("", item.Message);
        Assert.Equal(default, item.CreatedAtUtc);
        Assert.True(page.HasNext);
        Assert.Equal(4, page.LastPage);
    }

    [Theory]
    [InlineData(500, "RUB", "500 ₽")]
    [InlineData(250.5, "RUB", "250.5 ₽")]
    [InlineData(10, "USD", "10 $")]
    [InlineData(3, "PLN", "3 PLN")]
    [InlineData(7, "", "7")]
    public void Formats_amounts(double amount, string currency, string expected) =>
        Assert.Equal(expected, Donation.FormatAmount((decimal)amount, currency));

    [Fact]
    public void Marking_done_also_marks_seen_but_unmarking_done_keeps_seen()
    {
        var donation = Make.D(1);
        Assert.True(donation.IsNew);

        donation.Done = true;
        Assert.True(donation.Seen);
        Assert.False(donation.IsNew);

        donation.Done = false;
        Assert.True(donation.Seen);
    }

    [Fact]
    public void Renaming_shows_the_custom_name_keeps_the_original_and_clearing_restores_it()
    {
        var donation = new Donation { Id = 1, Username = "Аноним", Amount = 100, Currency = "RUB", CreatedAtUtc = DateTime.UtcNow };
        Assert.Equal("Аноним", donation.DisplayName);
        Assert.False(donation.IsRenamed);

        donation.Rename("  Вася  ");
        Assert.Equal("Вася", donation.DisplayName);
        Assert.Equal("Аноним", donation.Username);
        Assert.True(donation.IsRenamed);
        Assert.Contains("в DonationAlerts: Аноним", donation.SubText);
        Assert.StartsWith("Вася · ", donation.Summary);

        donation.Rename("   ");
        Assert.Equal("Аноним", donation.DisplayName);
        Assert.False(donation.IsRenamed);
        Assert.DoesNotContain("в DonationAlerts", donation.SubText);

        donation.Rename("Вася");
        donation.Rename("Аноним");
        Assert.False(donation.IsRenamed);
    }

    [Fact]
    public void A_donor_with_an_empty_name_gets_a_placeholder()
    {
        Assert.Equal("Без имени", new Donation { Username = "" }.DisplayName);
    }

    [Fact]
    public void A_custom_name_is_saved_and_survives_a_restart_without_touching_the_original()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        var store = new DonationStore(path);
        store.AddRange(new[] { Make.D(1), Make.D(2) });
        var changed = new List<string?>();
        store.Items[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.Items[0].Rename("Новое имя");

        Assert.Contains(nameof(Donation.DisplayName), changed);
        var reloaded = new DonationStore(path);
        Assert.Equal("Новое имя", reloaded.Items[0].DisplayName);
        Assert.Equal("u2", reloaded.Items[0].Username);
        Assert.Equal("u1", reloaded.Items[1].DisplayName);
    }

    [Fact]
    public void Old_files_without_a_custom_name_still_load()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        File.WriteAllText(path, "[{\"Id\":5,\"Username\":\"Old\",\"Amount\":10,\"Currency\":\"RUB\",\"Message\":\"\",\"CreatedAtUtc\":\"2026-01-01T00:00:00Z\",\"Seen\":true,\"Done\":false}]");

        var store = new DonationStore(path);

        Assert.Equal("Old", Assert.Single(store.Items).DisplayName);
    }

    [Fact]
    public void Secrets_round_trip_and_are_not_stored_in_plain_text()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ClientSecret = "top-secret-value";
        store.Current.AccessToken = "tok-abc";
        store.Save();

        var raw = File.ReadAllText(dir.File("settings.json"));
        Assert.DoesNotContain("top-secret-value", raw);
        Assert.DoesNotContain("tok-abc", raw);

        var reloaded = new SettingsStore(dir.File("settings.json"));
        Assert.Equal("top-secret-value", reloaded.Current.ClientSecret);
        Assert.Equal("tok-abc", reloaded.Current.AccessToken);
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{ not json");

        var store = new SettingsStore(dir.File("settings.json"));

        Assert.Equal(7653, store.Current.RedirectPort);
        Assert.False(store.Current.HasTokens);
        Assert.False(store.Current.UsesCodeFlow);
    }
}

public class ClientTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task Sends_the_bearer_token_and_returns_parsed_donations()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => (200, Make.PageJson(1, 1, (5, "Ann", "hi")));
        var client = new DonationAlertsClient(Make.Settings(dir), NewHttp(), da.Endpoints);

        var page = await client.GetDonationsAsync(1, CancellationToken.None);

        Assert.Equal("Ann", Assert.Single(page.Items).Username);
        Assert.Contains("auth=Bearer access-1", Assert.Single(da.Requests));
    }

    [Fact]
    public async Task Refreshes_the_token_once_on_401_and_retries()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            if (req.Url!.AbsolutePath == "/oauth/token")
            {
                Assert.Contains("grant_type=refresh_token", body);
                Assert.Contains("refresh_token=refresh-1", body);
                return (200, "{\"access_token\":\"access-2\",\"refresh_token\":\"refresh-2\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            }
            return req.Headers["Authorization"] == "Bearer access-2"
                ? (200, Make.PageJson(1, 1, (1, "x", "")))
                : (401, "{\"message\":\"Unauthenticated.\"}");
        };
        var settings = Make.Settings(dir);
        var client = new DonationAlertsClient(settings, NewHttp(), da.Endpoints);

        var page = await client.GetDonationsAsync(1, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal("access-2", settings.Current.AccessToken);
        Assert.Equal("refresh-2", settings.Current.RefreshToken);
        Assert.Equal(3, da.Requests.Count);
    }

    [Fact]
    public async Task Refreshes_before_the_request_when_the_token_has_expired()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => req.Url!.AbsolutePath == "/oauth/token"
            ? (200, "{\"access_token\":\"fresh\",\"refresh_token\":\"r\",\"expires_in\":3600}")
            : (200, Make.PageJson(1, 1));
        var settings = Make.Settings(dir);
        settings.Current.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(-5);
        var client = new DonationAlertsClient(settings, NewHttp(), da.Endpoints);

        await client.GetDonationsAsync(1, CancellationToken.None);

        Assert.StartsWith("POST /oauth/token", da.Requests[0]);
        Assert.Contains("auth=Bearer fresh", da.Requests[1]);
    }

    [Fact]
    public async Task A_rejected_refresh_token_clears_the_tokens_and_asks_to_log_in_again()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => req.Url!.AbsolutePath == "/oauth/token"
            ? (400, "{\"error\":\"invalid_grant\"}")
            : (401, "{}");
        var settings = Make.Settings(dir);
        var client = new DonationAlertsClient(settings, NewHttp(), da.Endpoints);

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetDonationsAsync(1, CancellationToken.None));

        Assert.False(settings.Current.HasTokens);
        Assert.True(settings.Current.HasCredentials);
    }

    [Fact]
    public async Task Without_tokens_it_asks_to_connect_and_makes_no_request()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var settings = new SettingsStore(dir.File("settings.json"));
        var client = new DonationAlertsClient(settings, NewHttp(), da.Endpoints);

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetDonationsAsync(1, CancellationToken.None));

        Assert.Empty(da.Requests);
    }

    [Fact]
    public async Task Rate_limit_and_server_errors_are_plain_http_errors_not_login_prompts()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var status = 429;
        da.Handler = (_, _) => (status, "{}");
        var client = new DonationAlertsClient(Make.Settings(dir), NewHttp(), da.Endpoints);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDonationsAsync(1, CancellationToken.None));
        status = 500;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetDonationsAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Exchanges_the_authorization_code_and_stores_the_tokens()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, body) =>
        {
            Assert.Contains("grant_type=authorization_code", body);
            Assert.Contains("code=the-code", body);
            Assert.Contains("client_secret=secret", body);
            Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A7653%2Fcallback", body);
            return (200, "{\"access_token\":\"A\",\"refresh_token\":\"R\",\"expires_in\":7200}");
        };
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.ClientId = "client";
        settings.Current.ClientSecret = "secret";
        var client = new DonationAlertsClient(settings, NewHttp(), da.Endpoints);

        await client.ExchangeCodeAsync("the-code", CancellationToken.None);

        Assert.Equal("A", settings.Current.AccessToken);
        Assert.Equal("R", settings.Current.RefreshToken);
        Assert.True(settings.Current.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(100));
    }

    [Fact]
    public void Authorize_url_carries_client_redirect_scope_and_state()
    {
        using var dir = new TempDir();
        var client = new DonationAlertsClient(Make.Settings(dir), NewHttp());

        var url = client.BuildAuthorizeUrl("st4te");

        Assert.StartsWith("https://www.donationalerts.com/oauth/authorize?", url);
        Assert.Contains("client_id=client", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("scope=oauth-donation-index", url);
        Assert.Contains("state=st4te", url);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A7653%2Fcallback", url);
    }
}

public class LoopbackTests
{
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Returns_the_code_and_ignores_stray_or_wrongly_stated_requests()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForCodeAsync(port, "good", () =>
        {
            _ = Task.Run(async () =>
            {
                var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
                Assert.Equal(System.Net.HttpStatusCode.NotFound, favicon.StatusCode);
                var wrong = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=evil&state=bad");
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrong.StatusCode);
                var good = await http.GetAsync($"http://127.0.0.1:{port}/callback?code=abc123&state=good");
                Assert.Equal(System.Net.HttpStatusCode.OK, good.StatusCode);
            });
        }, cts.Token);

        Assert.Equal("abc123", await wait);
    }

    [Fact]
    public async Task Surfaces_an_error_from_the_provider()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForCodeAsync(port, "s", () =>
        {
            _ = Task.Run(() => http.GetAsync($"http://127.0.0.1:{port}/callback?error=access_denied&state=s"));
        }, cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OAuthLoopback.WaitForCodeAsync(port, "s", () => { }, cts.Token));
    }
}

public class StoreTests
{
    [Fact]
    public void Adds_newest_first_dedupes_counts_unseen_and_persists()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        var store = new DonationStore(path);

        store.AddRange(new[] { Make.D(1), Make.D(2), Make.D(3) });
        var again = store.AddRange(new[] { Make.D(3), Make.D(4) });

        Assert.Equal(new long[] { 4, 3, 2, 1 }, store.Items.Select(d => d.Id).ToArray());
        Assert.Single(again);
        Assert.Equal(4, store.UnseenCount);

        store.Items[0].Seen = true;
        store.Items[1].Done = true;
        Assert.Equal(2, store.UnseenCount);

        var reloaded = new DonationStore(path);
        Assert.Equal(new long[] { 4, 3, 2, 1 }, reloaded.Items.Select(d => d.Id).ToArray());
        Assert.True(reloaded.Items[0].Seen);
        Assert.True(reloaded.Items[1].Done);
        Assert.Equal(2, reloaded.UnseenCount);
    }

    [Fact]
    public void Remove_and_restore_put_the_item_back_in_place_and_persist()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        var store = new DonationStore(path);
        store.AddRange(new[] { Make.D(1), Make.D(2), Make.D(3) });
        var victim = store.Items[1];

        var index = store.Remove(victim);
        Assert.Equal(1, index);
        Assert.Equal(new long[] { 3, 1 }, new DonationStore(path).Items.Select(d => d.Id).ToArray());

        store.Restore(victim, index);
        Assert.Equal(new long[] { 3, 2, 1 }, store.Items.Select(d => d.Id).ToArray());
        Assert.Equal(new long[] { 3, 2, 1 }, new DonationStore(path).Items.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void A_removed_donation_is_not_brought_back_by_a_later_add_of_older_ids()
    {
        using var dir = new TempDir();
        var store = new DonationStore(dir.File("donations.json"));
        store.AddRange(new[] { Make.D(1), Make.D(2) });
        store.Remove(store.Items[0]);

        store.AddRange(new[] { Make.D(3) });

        Assert.Equal(new long[] { 3, 1 }, store.Items.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void A_local_test_donation_stays_on_top_after_a_restart_and_is_never_confused_with_real_ones()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        var store = new DonationStore(path);
        store.AddRange(new[] { Make.D(50), Make.D(51) });
        store.AddRange(new[] { new Donation { Id = -DateTime.UtcNow.Ticks, Username = "test" } });

        var reloaded = new DonationStore(path);

        Assert.Equal("test", reloaded.Items[0].Username);
        Assert.Equal(new long[] { 51, 50 }, reloaded.Items.Skip(1).Select(d => d.Id).ToArray());
    }

    [Fact]
    public void A_corrupt_donations_file_starts_empty_and_keeps_a_copy()
    {
        using var dir = new TempDir();
        var path = dir.File("donations.json");
        File.WriteAllText(path, "[ broken");

        var store = new DonationStore(path);

        Assert.Empty(store.Items);
        Assert.True(File.Exists(path + ".broken"));
    }
}
