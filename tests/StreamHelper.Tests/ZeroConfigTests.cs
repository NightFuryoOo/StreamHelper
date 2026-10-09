using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class BuiltInCredentialTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    [Fact]
    public void Without_own_keys_the_built_in_application_ids_are_used_and_count_as_credentials()
    {
        var settings = new AppSettings();

        Assert.Equal(BuiltInCredentials.DonationAlertsClientId, settings.EffectiveClientId);
        Assert.Equal(BuiltInCredentials.TwitchClientId, settings.EffectiveTwitchClientId);
        Assert.True(settings.HasCredentials);
        Assert.True(settings.HasTwitchCredentials);
        Assert.False(settings.UsesCodeFlow);
        Assert.False(string.IsNullOrWhiteSpace(BuiltInCredentials.DonationAlertsClientId));
        Assert.False(string.IsNullOrWhiteSpace(BuiltInCredentials.TwitchClientId));
    }

    [Fact]
    public void Own_keys_override_the_built_in_ones_and_a_secret_selects_the_code_flow()
    {
        var settings = new AppSettings { ClientId = "  own-id  ", TwitchClientId = " own-twitch " };

        Assert.Equal("own-id", settings.EffectiveClientId);
        Assert.Equal("own-twitch", settings.EffectiveTwitchClientId);
        Assert.False(settings.UsesCodeFlow);

        settings.ClientSecret = "secret";
        Assert.True(settings.UsesCodeFlow);

        var secretWithoutId = new AppSettings { ClientSecret = "secret" };
        Assert.False(secretWithoutId.UsesCodeFlow);
        Assert.Equal(BuiltInCredentials.DonationAlertsClientId, secretWithoutId.EffectiveClientId);
    }

    [Fact]
    public void The_authorize_url_uses_the_implicit_grant_without_a_secret_and_the_code_grant_with_one()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var client = new DonationAlertsClient(store, NewHttp());

        var implicitUrl = client.BuildAuthorizeUrl("st");
        Assert.Contains("response_type=token", implicitUrl);
        Assert.Contains($"client_id={BuiltInCredentials.DonationAlertsClientId}", implicitUrl);
        Assert.Contains("scope=oauth-donation-index", implicitUrl);
        Assert.Contains("state=st", implicitUrl);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A7653%2Fcallback", implicitUrl);

        store.Current.ClientId = "own";
        store.Current.ClientSecret = "secret";
        var codeUrl = client.BuildAuthorizeUrl("st");
        Assert.Contains("response_type=code", codeUrl);
        Assert.Contains("client_id=own", codeUrl);
    }

    [Fact]
    public async Task An_implicit_token_is_stored_without_a_refresh_token_and_used_for_requests()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => (200, Make.PageJson(1, 1, (5, "Ann", "hi")));
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.RefreshToken = "leftover";
        var client = new DonationAlertsClient(store, NewHttp(), da.Endpoints);

        client.StoreImplicitToken("imp-token", 3600);
        var page = await client.GetDonationsAsync(1, CancellationToken.None);

        Assert.Equal("", store.Current.RefreshToken);
        Assert.Equal("imp-token", store.Current.AccessToken);
        Assert.True(store.Current.HasTokens);
        Assert.InRange(store.Current.AccessTokenExpiresUtc, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.Contains("auth=Bearer imp-token", Assert.Single(da.Requests));
        Assert.Single(page.Items);
    }

    [Fact]
    public void A_missing_or_absurd_lifetime_gets_a_sane_expiry()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var client = new DonationAlertsClient(store, NewHttp());

        client.StoreImplicitToken("a", null);
        Assert.InRange(store.Current.AccessTokenExpiresUtc, DateTime.UtcNow.AddDays(360), DateTime.UtcNow.AddDays(370));

        client.StoreImplicitToken("a", 99_999_999_999);
        Assert.True(store.Current.AccessTokenExpiresUtc < DateTime.UtcNow.AddYears(21));

        Assert.Throws<AuthRequiredException>(() => client.StoreImplicitToken("  ", 10));
    }

    [Fact]
    public async Task An_expired_implicit_token_asks_to_connect_again_instead_of_failing_obscurely()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (401, "{}");
        var store = new SettingsStore(dir.File("settings.json"));
        var client = new DonationAlertsClient(store, NewHttp(), da.Endpoints);
        client.StoreImplicitToken("imp-token", 3600);

        var ex = await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetDonationsAsync(1, CancellationToken.None));

        Assert.Contains("Подключить", ex.Message);
        Assert.Single(da.Requests);
    }

    [Fact]
    public async Task The_twitch_device_flow_uses_the_built_in_client_id_when_none_is_set()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, body) =>
        {
            Assert.Contains($"client_id={BuiltInCredentials.TwitchClientId}", body);
            return (200, "{\"device_code\":\"dc\",\"expires_in\":600,\"interval\":0,\"user_code\":\"C\",\"verification_uri\":\"u\"}");
        };
        var store = new SettingsStore(dir.File("settings.json"));
        var client = new TwitchClient(store, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var info = await client.StartDeviceFlowAsync(CancellationToken.None);

        Assert.Equal("C", info.UserCode);
    }

    [Fact]
    public void A_fresh_install_is_ready_to_connect_but_not_connected()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json")).Current;

        Assert.True(settings.HasCredentials && settings.HasTwitchCredentials);
        Assert.False(settings.HasTokens);
        Assert.False(settings.HasTwitchTokens);
    }
}

public class ImplicitLoopbackTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static StringContent Fragment(string text) => new(text, Encoding.UTF8, "text/plain");

    [Fact]
    public async Task The_callback_page_relays_the_fragment_and_the_token_is_returned()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForTokenAsync(port, "good", () =>
        {
            _ = Task.Run(async () =>
            {
                var page = await http.GetStringAsync($"http://127.0.0.1:{port}/callback");
                Assert.Contains("location.hash", page);
                Assert.Contains("/token", page);
                Assert.Contains("replaceState", page);
                var post = await http.PostAsync($"http://127.0.0.1:{port}/token",
                    Fragment("access_token=T0K%3Den&token_type=Bearer&expires_in=3600&state=good"));
                Assert.Equal(HttpStatusCode.OK, post.StatusCode);
            });
        }, cts.Token);

        var token = await wait;

        Assert.Equal("T0K=en", token.AccessToken);
        Assert.Equal(3600, token.ExpiresInSeconds);
    }

    [Fact]
    public async Task A_missing_lifetime_is_reported_as_unknown()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForTokenAsync(port, "s", () =>
        {
            _ = Task.Run(() => http.PostAsync($"http://127.0.0.1:{port}/token", Fragment("access_token=abc&state=s")));
        }, cts.Token);

        var token = await wait;

        Assert.Equal("abc", token.AccessToken);
        Assert.Null(token.ExpiresInSeconds);
    }

    [Fact]
    public async Task A_wrong_state_a_foreign_origin_and_stray_requests_are_rejected_and_the_wait_continues()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForTokenAsync(port, "good", () =>
        {
            _ = Task.Run(async () =>
            {
                var url = $"http://127.0.0.1:{port}/token";
                var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
                Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);

                var wrongState = await http.PostAsync(url, Fragment("access_token=evil&state=bad"));
                Assert.Equal(HttpStatusCode.BadRequest, wrongState.StatusCode);

                var noState = await http.PostAsync(url, Fragment("access_token=evil"));
                Assert.Equal(HttpStatusCode.BadRequest, noState.StatusCode);

                var noToken = await http.PostAsync(url, Fragment("state=good"));
                Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

                using var foreign = new HttpRequestMessage(HttpMethod.Post, url) { Content = Fragment("access_token=evil&state=good") };
                foreign.Headers.Add("Origin", "https://evil.example");
                Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);

                using var own = new HttpRequestMessage(HttpMethod.Post, url) { Content = Fragment("access_token=real&state=good") };
                own.Headers.Add("Origin", $"http://127.0.0.1:{port}");
                Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(own)).StatusCode);
            });
        }, cts.Token);

        var token = await wait;

        Assert.Equal("real", token.AccessToken);
    }

    [Fact]
    public async Task An_error_from_the_provider_is_surfaced()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForTokenAsync(port, "s", () =>
        {
            _ = Task.Run(() => http.PostAsync($"http://127.0.0.1:{port}/token", Fragment("error=access_denied&state=s")));
        }, cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task An_error_in_the_query_of_the_callback_is_surfaced_too()
    {
        var port = FreePort();
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = OAuthLoopback.WaitForTokenAsync(port, "s", () =>
        {
            _ = Task.Run(() => http.GetAsync($"http://127.0.0.1:{port}/callback?error=access_denied"));
        }, cts.Token);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task Cancellation_stops_the_wait_and_a_busy_port_gives_a_clear_message()
    {
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => OAuthLoopback.WaitForTokenAsync(port, "s", () => { }, cts.Token));

        using var occupied = new HttpListener();
        occupied.Prefixes.Add($"http://127.0.0.1:{port}/");
        occupied.Start();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OAuthLoopback.WaitForTokenAsync(port, "s", () => { }, CancellationToken.None));
        Assert.Contains(port.ToString(), ex.Message);
    }
}
