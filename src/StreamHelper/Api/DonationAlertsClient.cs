using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Api;

public sealed record DaEndpoints(string AuthorizeUrl, string TokenUrl, string DonationsUrl)
{
    public static readonly DaEndpoints Default = new(
        "https://www.donationalerts.com/oauth/authorize",
        "https://www.donationalerts.com/oauth/token",
        "https://www.donationalerts.com/api/v1/alerts/donations");
}

public sealed record DonationPage(IReadOnlyList<Donation> Items, bool HasNext, int CurrentPage, int LastPage);

public interface IDonationSource
{
    Task<DonationPage> GetDonationsAsync(int page, CancellationToken ct);
}

public sealed class AuthRequiredException : Exception
{
    public AuthRequiredException(string message) : base(message)
    {
    }
}

public sealed class DonationAlertsClient : IDonationSource
{
    public const string Scope = "oauth-donation-index";

    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly DaEndpoints _endpoints;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public DonationAlertsClient(SettingsStore store, HttpClient http, DaEndpoints? endpoints = null)
    {
        _store = store;
        _http = http;
        _endpoints = endpoints ?? DaEndpoints.Default;
    }

    private AppSettings Settings => _store.Current;

    public string BuildAuthorizeUrl(string state) =>
        $"{_endpoints.AuthorizeUrl}?client_id={Uri.EscapeDataString(Settings.EffectiveClientId)}" +
        $"&redirect_uri={Uri.EscapeDataString(Settings.RedirectUri)}" +
        $"&response_type={(Settings.UsesCodeFlow ? "code" : "token")}&scope={Uri.EscapeDataString(Scope)}&state={Uri.EscapeDataString(state)}";

    public void StoreImplicitToken(string accessToken, long? expiresInSeconds)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) throw new AuthRequiredException("DonationAlerts вернул пустой токен.");
        Settings.AccessToken = accessToken.Trim();
        Settings.RefreshToken = "";
        var seconds = expiresInSeconds is > 0 ? expiresInSeconds.Value : 365L * 24 * 3600;
        Settings.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Min(seconds, 20L * 365 * 24 * 3600));
        _store.Save();
    }

    public async Task ExchangeCodeAsync(string code, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = Settings.EffectiveClientId,
            ["client_secret"] = Settings.ClientSecret,
            ["redirect_uri"] = Settings.RedirectUri,
            ["code"] = code,
        };
        var (status, body) = await PostTokenAsync(form, ct);
        if (status != HttpStatusCode.OK)
        {
            throw new AuthRequiredException($"DonationAlerts не принял код авторизации (HTTP {(int)status}): {Trim(body)}");
        }
        StoreTokens(body);
    }

    public async Task<DonationPage> GetDonationsAsync(int page, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        var (status, body) = await GetAsync(page, token, ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            token = await RefreshAsync(ct, token);
            (status, body) = await GetAsync(page, token, ct);
            if (status == HttpStatusCode.Unauthorized)
            {
                throw new AuthRequiredException("DonationAlerts отклонил токен, нужно подключиться заново.");
            }
        }
        if (status == (HttpStatusCode)429)
        {
            throw new HttpRequestException("DonationAlerts: слишком много запросов (HTTP 429).");
        }
        if (status != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"DonationAlerts: HTTP {(int)status}.");
        }
        return ParseDonations(body);
    }

    private async Task<(HttpStatusCode, string)> GetAsync(int page, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_endpoints.DonationsUrl}?page={page}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!Settings.HasTokens) throw new AuthRequiredException("DonationAlerts ещё не подключён.");
        var token = Settings.AccessToken;
        if (token.Length > 0 && Settings.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return token;
        return await RefreshAsync(ct, token);
    }

    private async Task<string> RefreshAsync(CancellationToken ct, string staleToken)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var current = Settings.AccessToken;
            if (current.Length > 0 && current != staleToken && Settings.AccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return current;
            }

            var refresh = Settings.RefreshToken;
            if (refresh.Length == 0)
            {
                throw new AuthRequiredException("Доступ к DonationAlerts истёк или отозван: нажми «Подключить» в настройках, вкладка «Донаты».");
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = Settings.EffectiveClientId,
                ["client_secret"] = Settings.ClientSecret,
                ["scope"] = Scope,
            };
            var (status, body) = await PostTokenAsync(form, ct);
            if (status == HttpStatusCode.OK)
            {
                StoreTokens(body);
                return Settings.AccessToken;
            }
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Settings.AccessToken = "";
                Settings.RefreshToken = "";
                _store.Save();
                throw new AuthRequiredException("Доступ истёк или отозван, нужно подключиться заново.");
            }
            throw new HttpRequestException($"DonationAlerts: не удалось обновить токен (HTTP {(int)status}).");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<(HttpStatusCode, string)> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(_endpoints.TokenUrl, content, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private void StoreTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? "";
        if (access.Length == 0) throw new AuthRequiredException("DonationAlerts вернул пустой токен.");
        Settings.AccessToken = access;
        if (root.TryGetProperty("refresh_token", out var refresh) && refresh.ValueKind == JsonValueKind.String)
        {
            Settings.RefreshToken = refresh.GetString() ?? "";
        }
        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var value) ? value : 3600;
        Settings.AccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        _store.Save();
    }

    public static DonationPage ParseDonations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<Donation>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                if (!element.TryGetProperty("id", out var id) || !id.TryGetInt64(out var idValue)) continue;
                items.Add(new Donation
                {
                    Id = idValue,
                    Username = ReadString(element, "username"),
                    Message = ReadString(element, "message"),
                    Currency = ReadString(element, "currency"),
                    Amount = ReadDecimal(element, "amount"),
                    CreatedAtUtc = ReadTimestamp(element, "created_at"),
                });
            }
        }

        var hasNext = false;
        if (root.TryGetProperty("links", out var links) && links.TryGetProperty("next", out var next))
        {
            hasNext = next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString());
        }

        int current = 1, last = 1;
        if (root.TryGetProperty("meta", out var meta))
        {
            if (meta.TryGetProperty("current_page", out var c) && c.TryGetInt32(out var cv)) current = cv;
            if (meta.TryGetProperty("last_page", out var l) && l.TryGetInt32(out var lv)) last = lv;
        }
        return new DonationPage(items, hasNext, current, Math.Max(last, current));
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static DateTime ReadTimestamp(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        if (DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
            ? parsed
            : default;
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200];
}
