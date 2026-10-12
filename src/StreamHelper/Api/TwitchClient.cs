using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Api;

public sealed record TwitchEndpoints(
    string DeviceUrl, string TokenUrl, string UsersUrl, string FollowersUrl, string EventSubUrl, string EventSubSubscriptionsUrl,
    string RewardsUrl, string EmotesBase = "https://static-cdn.jtvnw.net/emoticons/v2")
{
    public static readonly TwitchEndpoints Default = new(
        "https://id.twitch.tv/oauth2/device",
        "https://id.twitch.tv/oauth2/token",
        "https://api.twitch.tv/helix/users",
        "https://api.twitch.tv/helix/channels/followers",
        "wss://eventsub.wss.twitch.tv/ws",
        "https://api.twitch.tv/helix/eventsub/subscriptions",
        "https://api.twitch.tv/helix/channel_points/custom_rewards");

    private string Helix(string path) => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/" + path;

    public string ChatBadgesUrl => Helix("chat/badges");

    public string EmoteUrl(string id, bool animated, bool giant = false) =>
        $"{EmotesBase}/{Uri.EscapeDataString(id)}/{(animated ? "animated" : "static")}/dark/{(giant ? "3.0" : "2.0")}";

    public string StreamsUrl => Helix("streams");

    public string ClipsUrl => Helix("clips");

    public string ModerationBansUrl => Helix("moderation/bans");

    public string PollsUrl => Helix("polls");

    public string PredictionsUrl => Helix("predictions");

    public string ShoutoutsUrl => Helix("chat/shoutouts");

    public string ModeratedChannelsUrl => Helix("moderation/channels");

    public string ChatChattersUrl => Helix("chat/chatters");

    public string ChatEmotesUrl => Helix("chat/emotes");

    public string ChatMessagesUrl => Helix("chat/messages");

    public string ModerationChatUrl => Helix("moderation/chat");

    public static TwitchEndpoints FromBase(string baseUrl)
    {
        baseUrl = baseUrl.TrimEnd('/');
        var wsBase = baseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" + baseUrl[5..] : "ws" + baseUrl[4..];
        return new TwitchEndpoints(
            baseUrl + "/oauth2/device",
            baseUrl + "/oauth2/token",
            baseUrl + "/helix/users",
            baseUrl + "/helix/channels/followers",
            wsBase + "/ws",
            baseUrl + "/helix/eventsub/subscriptions",
            baseUrl + "/helix/channel_points/custom_rewards",
            baseUrl + "/emoticons/v2");
    }
}

public sealed record DownloadedImage(byte[] Data, string? ContentType);

public interface IStreamApi
{
    Task<DateTime?> GetStreamStartAsync(CancellationToken ct);
}

public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

public sealed record FollowerPage(IReadOnlyList<Follower> Items, string? Cursor);

public interface IFollowerSource
{
    Task<FollowerPage> GetFollowersAsync(string? cursor, CancellationToken ct);
}

public sealed record LiveStream(string UserId, string Login, string Game, string Title, int Viewers, DateTime? StartedUtc);

public interface ILiveStreamApi
{
    Task<IReadOnlyList<LiveStream>> GetLiveStreamsAsync(IReadOnlyList<string> userIds, CancellationToken ct);
}

public sealed partial class TwitchClient : IFollowerSource, IEventSubApi, IRewardApi, IBadgeApi, IModerationApi, IStreamApi, IVoteApi, IChatSendApi, IEmoteApi, IChattersApi, ILiveStreamApi, IClipApi
{
    public const string FollowerScope = "moderator:read:followers";
    public const string SubscriptionScope = "channel:read:subscriptions";
    public const string RedemptionScope = "channel:read:redemptions";
    public const string ManageScope = "channel:manage:redemptions";
    public const string ChatScope = "user:read:chat";
    public const string ModerateScope = "moderator:manage:banned_users";
    public const string PollScope = "channel:manage:polls";
    public const string PredictionScope = "channel:manage:predictions";
    public const string ShoutoutScope = "moderator:manage:shoutouts";
    public const string ModeratedChannelsScope = "user:read:moderated_channels";
    public const string ChatWriteScope = "user:write:chat";
    public const string DeleteMessagesScope = "moderator:manage:chat_messages";
    public const string BitsScope = "bits:read";
    public const string EmotesScope = "user:read:emotes";
    public const string ChattersScope = "moderator:read:chatters";
    public const int MaxChatMessage = 500;
    public const string Scopes = FollowerScope + " " + SubscriptionScope + " " + RedemptionScope + " " + ManageScope + " " + ChatScope + " " + ModerateScope +
                                 " " + PollScope + " " + PredictionScope + " " + ShoutoutScope + " " + ModeratedChannelsScope + " " + ChatWriteScope + " " + DeleteMessagesScope +
                                 " " + BitsScope + " " + EmotesScope + " " + ChattersScope;
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly TwitchEndpoints _endpoints;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public TwitchClient(SettingsStore store, HttpClient http, TwitchEndpoints? endpoints = null)
    {
        _store = store;
        _http = http;
        _endpoints = endpoints ?? TwitchEndpoints.Default;
    }

    private AppSettings Settings => _store.Current;

    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(CancellationToken ct)
    {
        var (status, body) = await PostFormAsync(_endpoints.DeviceUrl, new Dictionary<string, string>
        {
            ["client_id"] = Settings.EffectiveTwitchClientId,
            ["scopes"] = Scopes,
        }, ct);
        if (status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Twitch не выдал код для входа (HTTP {(int)status}): {ReadMessage(body)}");
        }
        return ParseDeviceCode(body);
    }

    public async Task CompleteDeviceFlowAsync(DeviceCodeInfo info, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(info.ExpiresInSeconds, 1));
        var interval = TimeSpan.FromSeconds(Math.Max(info.IntervalSeconds, 0));
        while (true)
        {
            await Task.Delay(interval, ct);
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("Время на подтверждение входа в Twitch истекло.");

            var (status, body) = await PostFormAsync(_endpoints.TokenUrl, new Dictionary<string, string>
            {
                ["client_id"] = Settings.EffectiveTwitchClientId,
                ["scopes"] = Scopes,
                ["device_code"] = info.DeviceCode,
                ["grant_type"] = DeviceGrant,
            }, ct);

            if (status == HttpStatusCode.OK)
            {
                StoreTokens(body);
                await FetchUserAsync(ct);
                return;
            }

            var message = ReadMessage(body);
            if (message.Contains("authorization_pending", StringComparison.OrdinalIgnoreCase)) continue;
            if (message.Contains("slow_down", StringComparison.OrdinalIgnoreCase))
            {
                interval += TimeSpan.FromSeconds(5);
                continue;
            }
            throw new InvalidOperationException($"Twitch отклонил вход (HTTP {(int)status}): {message}");
        }
    }

    public async Task<FollowerPage> GetFollowersAsync(string? cursor, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        var (status, body) = await GetFollowersRawAsync(cursor, token, ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            token = await RefreshAsync(ct, token);
            (status, body) = await GetFollowersRawAsync(cursor, token, ct);
        }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthRequiredException(
                "Twitch не даёт список фолловеров: подключи аккаунт владельца канала заново (нужно право moderator:read:followers).");
        }
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseFollowers(body);
    }

    private string? OtherChannel => Settings.IsOwnChannel ? null : Settings.ChannelLabel;

    public async Task<DateTime?> GetStreamStartAsync(CancellationToken ct)
    {
        var url = _endpoints.StreamsUrl + "?user_id=" + Uri.EscapeDataString(Settings.ChannelId);
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не сказал, идёт ли стрим: переподключи Twitch.");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseStreamStart(body);
    }

    public static DateTime? ParseStreamStart(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var stream in data.EnumerateArray())
            {
                var started = ReadString(stream, "started_at");
                if (started.Length > 0 && DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)) return at;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public async Task<IReadOnlyList<LiveStream>> GetLiveStreamsAsync(IReadOnlyList<string> userIds, CancellationToken ct)
    {
        var live = new List<LiveStream>();
        var ids = userIds.Where(id => id.Length > 0).Distinct().ToList();
        for (var i = 0; i < ids.Count; i += 100)
        {
            var batch = ids.Skip(i).Take(100);
            var url = _endpoints.StreamsUrl + "?" + string.Join("&", batch.Select(id => "user_id=" + Uri.EscapeDataString(id))) + "&first=100";
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            ThrowIfUnauthorized(status, "Twitch не сказал, кто в эфире: переподключи Twitch.");
            if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
            live.AddRange(ParseLiveStreams(body));
        }
        return live;
    }

    public async Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct)
    {
        var url = _endpoints.ClipsUrl + "?id=" + Uri.EscapeDataString(slug);
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не отдал клип: переподключи Twitch.");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return null;
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return PreviewLinks.ParseClip(body);
    }

    public static IReadOnlyList<LiveStream> ParseLiveStreams(string json)
    {
        var live = new List<LiveStream>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return live;
        foreach (var stream in data.EnumerateArray())
        {
            var id = ReadString(stream, "user_id");
            if (id.Length == 0 || ReadString(stream, "type") != "live") continue;
            var started = DateTime.TryParse(ReadString(stream, "started_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : (DateTime?)null;
            var viewers = stream.TryGetProperty("viewer_count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var n) ? n : 0;
            live.Add(new LiveStream(id, ReadString(stream, "user_login"), ReadString(stream, "game_name"), ReadString(stream, "title"), viewers, started));
        }
        return live;
    }

    public async Task<DownloadedImage?> DownloadImageAsync(string url, CancellationToken ct)    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;

        using var response = await _http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        if (data.Length == 0 || data.Length > 5_000_000) return null;
        return new DownloadedImage(data, response.Content.Headers.ContentType?.MediaType);
    }

    private static void ThrowIfUnauthorized(HttpStatusCode status, string message)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AuthRequiredException(message);
    }

    private async Task<(HttpStatusCode, string)> CallWithRefreshAsync(
        Func<string, CancellationToken, Task<(HttpStatusCode, string)>> call, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        var result = await call(token, ct);
        if (result.Item1 != HttpStatusCode.Unauthorized) return result;
        token = await RefreshAsync(ct, token);
        return await call(token, ct);
    }

    private async Task<(HttpStatusCode, string)> SendJsonAsync(HttpMethod method, string url, string token, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json != null) request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", Settings.EffectiveTwitchClientId);
        using var response = await _http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<(HttpStatusCode, string)> GetFollowersRawAsync(string? cursor, string token, CancellationToken ct)
    {
        var url = $"{_endpoints.FollowersUrl}?broadcaster_id={Uri.EscapeDataString(Settings.ChannelId)}&first=100";
        if (!string.IsNullOrEmpty(cursor)) url += "&after=" + Uri.EscapeDataString(cursor);
        return await GetAsync(url, token, ct);
    }

    private async Task FetchUserAsync(CancellationToken ct)
    {
        var (status, body) = await GetAsync(_endpoints.UsersUrl, Settings.TwitchAccessToken, ct);
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: не удалось узнать пользователя (HTTP {(int)status}).");
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.GetProperty("data")[0];
        var userId = first.GetProperty("id").GetString() ?? "";
        if (userId != Settings.TwitchUserId)
        {
            Settings.FollowersBaselined = false;
            Settings.LastFollowerAtUtc = default;
            Settings.LastFollowerUserIds = new List<string>();
            Settings.TwitchChannelId = "";
            Settings.TwitchChannelLogin = "";
            Settings.TwitchChannelName = "";
            Settings.TwitchChannelAsViewer = false;
        }
        Settings.TwitchUserId = userId;
        Settings.TwitchLogin = ReadString(first, "login");
        _store.Save();
    }

    private async Task<(HttpStatusCode, string)> GetAsync(string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Client-Id", Settings.EffectiveTwitchClientId);
        using var response = await _http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!Settings.HasTwitchTokens) throw new AuthRequiredException("Twitch ещё не подключён.");
        var token = Settings.TwitchAccessToken;
        if (token.Length > 0 && Settings.TwitchAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1)) return token;
        return await RefreshAsync(ct, token);
    }

    private async Task<string> RefreshAsync(CancellationToken ct, string staleToken)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var current = Settings.TwitchAccessToken;
            if (current.Length > 0 && current != staleToken && Settings.TwitchAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return current;
            }

            var refresh = Settings.TwitchRefreshToken;
            if (refresh.Length == 0) throw new AuthRequiredException("Нет refresh-токена Twitch, нужно подключиться заново.");

            var (status, body) = await PostFormAsync(_endpoints.TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = Settings.EffectiveTwitchClientId,
            }, ct);
            if (status == HttpStatusCode.OK)
            {
                StoreTokens(body);
                return Settings.TwitchAccessToken;
            }
            if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Settings.TwitchAccessToken = "";
                Settings.TwitchRefreshToken = "";
                _store.Save();
                throw new AuthRequiredException("Вход в Twitch истёк или отозван, нужно подключиться заново.");
            }
            throw new HttpRequestException($"Twitch: не удалось обновить токен (HTTP {(int)status}).");
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<(HttpStatusCode, string)> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(url, content, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private void StoreTokens(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = ReadString(root, "access_token");
        if (access.Length == 0) throw new AuthRequiredException("Twitch вернул пустой токен.");
        Settings.TwitchAccessToken = access;
        var refresh = ReadString(root, "refresh_token");
        if (refresh.Length > 0) Settings.TwitchRefreshToken = refresh;
        if (root.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var item in scope.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name) names.Add(name);
            }
            Settings.TwitchScopes = string.Join(' ', names);
        }
        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var value) ? value : 3600;
        Settings.TwitchAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        _store.Save();
    }

    public static DeviceCodeInfo ParseDeviceCode(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new DeviceCodeInfo(
            ReadString(root, "device_code"),
            ReadString(root, "user_code"),
            ReadString(root, "verification_uri"),
            root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ev) ? ev : 1800,
            root.TryGetProperty("interval", out var i) && i.TryGetInt32(out var iv) ? iv : 5);
    }

    public static FollowerPage ParseFollowers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<Follower>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                var userId = ReadString(element, "user_id");
                if (userId.Length == 0) continue;
                items.Add(new Follower
                {
                    UserId = userId,
                    Login = ReadString(element, "user_login"),
                    DisplayName = ReadString(element, "user_name"),
                    FollowedAtUtc = ReadTimestamp(element, "followed_at"),
                });
            }
        }

        string? cursor = null;
        if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object &&
            pagination.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.String)
        {
            cursor = c.GetString();
            if (string.IsNullOrEmpty(cursor)) cursor = null;
        }
        return new FollowerPage(items, cursor);
    }

    private static string? ReadImageUrl(JsonElement element, string name)
    {
        if (!element.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object) return null;
        var url = ReadString(image, name);
        return url.Length > 0 ? url : null;
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? ReadLimit(JsonElement element, string objectName, string valueName)
    {
        if (!element.TryGetProperty(objectName, out var setting) || setting.ValueKind != JsonValueKind.Object) return null;
        if (!ReadBool(setting, "is_enabled")) return null;
        return setting.TryGetProperty(valueName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 1
            ? n
            : null;
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static DateTime ReadTimestamp(JsonElement element, string name) =>
        DateTime.TryParse(ReadString(element, name), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : default;

    private static string ReadMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ReadString(doc.RootElement, "message");
        }
        catch (JsonException)
        {
            return body.Length <= 200 ? body : body[..200];
        }
    }
}
