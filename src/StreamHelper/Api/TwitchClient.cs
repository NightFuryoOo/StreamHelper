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

    public string ChatBadgesUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/chat/badges";

    public string EmoteUrl(string id, bool animated) =>
        $"{EmotesBase}/{Uri.EscapeDataString(id)}/{(animated ? "animated" : "static")}/dark/2.0";

    public string StreamsUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/streams";

    public string ModerationBansUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/moderation/bans";

    public string PollsUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/polls";

    public string PredictionsUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/predictions";

    public string ShoutoutsUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/chat/shoutouts";

    public string ModeratedChannelsUrl => UsersUrl[..UsersUrl.LastIndexOf("/helix/users", StringComparison.Ordinal)] + "/helix/moderation/channels";

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

public sealed record RewardInfo(string Id, string Title, long Cost, bool IsEnabled)
{
    public string Prompt { get; init; } = "";
    public string BackgroundColor { get; init; } = "";
    public bool IsUserInputRequired { get; init; }
    public int? MaxPerStream { get; init; }
    public int? MaxPerUserPerStream { get; init; }
    public int? GlobalCooldownSeconds { get; init; }
    public bool SkipRequestQueue { get; init; }

    public string? ImageUrl { get; init; }
    public string? ImageUrl1x { get; init; }
    public string? ImageUrl2x { get; init; }
    public string? ImageUrl4x { get; init; }
}

public sealed record DownloadedImage(byte[] Data, string? ContentType);

public enum RewardDeleteOutcome
{
    Deleted,
    NotFound,
    NotAllowed,
    Failed,
}

public sealed record RewardDeleteResult(RewardDeleteOutcome Outcome, string Message);

public enum RewardCreateOutcome
{
    Created,
    TitleTaken,
    LimitReached,
    Failed,
}

public sealed record RewardCreateResult(RewardCreateOutcome Outcome, string? Id, string Message);

public enum RedemptionDecision
{
    Fulfilled,
    Canceled,
}

public enum RedemptionUpdateOutcome
{
    Done,
    AlreadyProcessed,
    NotAllowed,
    NotFound,
    Failed,
}

public sealed record RedemptionUpdateResult(RedemptionUpdateOutcome Outcome, string Message);

public sealed record RewardToggleResult(bool Success, string Message, bool Missing = false);

public enum RewardRenameOutcome
{
    Renamed,
    TitleTaken,
    Failed,
}

public sealed record RewardRenameResult(RewardRenameOutcome Outcome, string Message);

public interface IRewardApi
{
    Task<IReadOnlyList<RewardInfo>> GetRewardsAsync(CancellationToken ct);

    Task<IReadOnlySet<string>> GetManageableRewardIdsAsync(CancellationToken ct);

    Task<DownloadedImage?> DownloadImageAsync(string url, CancellationToken ct);

    Task<RewardDeleteResult> DeleteRewardAsync(string rewardId, CancellationToken ct);

    Task<RewardCreateResult> CreateRewardAsync(RewardInfo template, string title, CancellationToken ct);

    Task<RewardToggleResult> SetRewardEnabledAsync(string rewardId, bool enabled, CancellationToken ct);

    Task<RewardRenameResult> SetRewardTitleAsync(string rewardId, string title, CancellationToken ct);

    Task<RedemptionUpdateResult> UpdateRedemptionAsync(
        string rewardId, string redemptionId, RedemptionDecision decision, CancellationToken ct);
}

public sealed record ModerationResult(bool Success, string Message);

public sealed record ModeratedChannel(string Id, string Login, string Name)
{
    public string Label => Name.Length > 0 ? Name : Login;
}

public interface IModerationApi
{
    Task<ModerationResult> BanAsync(string userId, int? durationSeconds, CancellationToken ct);

    Task<ModerationResult> UnbanAsync(string userId, CancellationToken ct);

    Task<ModerationResult> ShoutoutAsync(string userId, CancellationToken ct);
}

public sealed record VoteResult(bool Success, string Message, ChannelVote? Vote = null);

public enum PredictionChange
{
    Lock,
    Resolve,
    Cancel,
}

public interface IVoteApi
{
    Task<ChannelVote?> GetLatestPollAsync(CancellationToken ct);

    Task<ChannelVote?> GetLatestPredictionAsync(CancellationToken ct);

    Task<ChannelVote?> GetLastEndedAsync(VoteKind kind, CancellationToken ct);

    Task<VoteResult> CreatePollAsync(PollDraft draft, CancellationToken ct);

    Task<VoteResult> EndPollAsync(string pollId, CancellationToken ct);

    Task<VoteResult> CreatePredictionAsync(PredictionDraft draft, CancellationToken ct);

    Task<VoteResult> ChangePredictionAsync(string predictionId, PredictionChange change, string? winnerId, CancellationToken ct);
}

public interface IBadgeApi
{
    Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct);
}

public interface IStreamApi
{
    Task<DateTime?> GetStreamStartAsync(CancellationToken ct);
}

public interface IEventSubApi
{
    Task CreateSubscriptionAsync(string type, string sessionId, CancellationToken ct);
}

public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

public sealed record FollowerPage(IReadOnlyList<Follower> Items, string? Cursor);

public interface IFollowerSource
{
    Task<FollowerPage> GetFollowersAsync(string? cursor, CancellationToken ct);
}

public sealed class TwitchClient : IFollowerSource, IEventSubApi, IRewardApi, IBadgeApi, IModerationApi, IStreamApi, IVoteApi
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
    public const string Scopes = FollowerScope + " " + SubscriptionScope + " " + RedemptionScope + " " + ManageScope + " " + ChatScope + " " + ModerateScope +
                                 " " + PollScope + " " + PredictionScope + " " + ShoutoutScope + " " + ModeratedChannelsScope;
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

    public async Task<IReadOnlyList<RewardInfo>> GetRewardsAsync(CancellationToken ct)
    {
        if (!Settings.HasRedemptionScope)
        {
            throw new AuthRequiredException("Для списка наград переподключи Twitch в настройках: нужно право на награды за баллы.");
        }

        var token = await GetAccessTokenAsync(ct);
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        var (status, body) = await GetAsync(url, token, ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            token = await RefreshAsync(ct, token);
            (status, body) = await GetAsync(url, token, ct);
        }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthRequiredException(
                "Twitch не отдал список наград: переподключи Twitch (нужно право channel:read:redemptions; баллы канала должны быть доступны на канале).");
        }
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseRewards(body);
    }

    public static IReadOnlyList<RewardInfo> ParseRewards(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rewards = new List<RewardInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                var id = ReadString(element, "id");
                if (id.Length == 0) continue;
                var cost = element.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var cv) ? cv : 0;
                var enabled = !element.TryGetProperty("is_enabled", out var e) || e.ValueKind != JsonValueKind.False;
                rewards.Add(new RewardInfo(id, ReadString(element, "title"), cost, enabled)
                {
                    Prompt = ReadString(element, "prompt"),
                    BackgroundColor = ReadString(element, "background_color"),
                    IsUserInputRequired = ReadBool(element, "is_user_input_required"),
                    MaxPerStream = ReadLimit(element, "max_per_stream_setting", "max_per_stream"),
                    MaxPerUserPerStream = ReadLimit(element, "max_per_user_per_stream_setting", "max_per_user_per_stream"),
                    GlobalCooldownSeconds = ReadLimit(element, "global_cooldown_setting", "global_cooldown_seconds"),
                    SkipRequestQueue = ReadBool(element, "should_redemptions_skip_request_queue"),
                    ImageUrl = ReadImageUrl(element, "url_4x") ?? ReadImageUrl(element, "url_2x") ?? ReadImageUrl(element, "url_1x"),
                    ImageUrl1x = ReadImageUrl(element, "url_1x"),
                    ImageUrl2x = ReadImageUrl(element, "url_2x"),
                    ImageUrl4x = ReadImageUrl(element, "url_4x"),
                });
            }
        }
        rewards.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        return rewards;
    }

    public Task<ModerationResult> BanAsync(string userId, int? durationSeconds, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(durationSeconds is > 0
            ? new { data = (object)new { user_id = userId, duration = durationSeconds.Value } }
            : new { data = (object)new { user_id = userId } });
        return ModerateAsync(HttpMethod.Post, json, userId, HttpStatusCode.OK, ct);
    }

    public Task<ModerationResult> UnbanAsync(string userId, CancellationToken ct) =>
        ModerateAsync(HttpMethod.Delete, null, userId, HttpStatusCode.NoContent, ct);

    private async Task<ModerationResult> ModerateAsync(HttpMethod method, string? json, string userId, HttpStatusCode success, CancellationToken ct)
    {
        if (!Settings.HasModerationScope) return new ModerationResult(false, "Нужно переподключить Twitch: новое право на модерацию.");
        if (userId.Length == 0) return new ModerationResult(false, "Не знаю, кого именно.");

        var url = $"{_endpoints.ModerationBansUrl}?broadcaster_id={Uri.EscapeDataString(Settings.ChannelId)}&moderator_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        if (method == HttpMethod.Delete) url += "&user_id=" + Uri.EscapeDataString(userId);
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(method, url, token, json, c), ct);
            if (status == success) return new ModerationResult(true, "");
            return new ModerationResult(false, DescribeModerationFailure(status, ReadMessage(body), OtherChannel));
        }
        catch (AuthRequiredException ex)
        {
            return new ModerationResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ModerationResult(false, "Нет связи с Twitch: " + ex.Message);
        }
    }

    public async Task<ModerationResult> ShoutoutAsync(string userId, CancellationToken ct)
    {
        if (!Settings.HasShoutoutScope) return new ModerationResult(false, "Нужно переподключить Twitch: новое право на отметки.");
        if (userId.Length == 0) return new ModerationResult(false, "Не знаю, кого именно.");

        var url = $"{_endpoints.ShoutoutsUrl}?from_broadcaster_id={Uri.EscapeDataString(Settings.ChannelId)}&to_broadcaster_id={Uri.EscapeDataString(userId)}" +
                  $"&moderator_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Post, url, token, null, c), ct);
            if (status is HttpStatusCode.NoContent or HttpStatusCode.OK) return new ModerationResult(true, "");
            return new ModerationResult(false, DescribeShoutoutFailure(status, ReadMessage(body), OtherChannel));
        }
        catch (AuthRequiredException ex)
        {
            return new ModerationResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ModerationResult(false, "Нет связи с Twitch: " + ex.Message);
        }
    }

    private string? OtherChannel => Settings.IsOwnChannel ? null : Settings.ChannelLabel;

    internal static string DescribeShoutoutFailure(HttpStatusCode status, string message, string? otherChannel = null)
    {
        if (status == HttpStatusCode.BadRequest)
        {
            if (message.Contains("live", StringComparison.OrdinalIgnoreCase) || message.Contains("viewers", StringComparison.OrdinalIgnoreCase))
            {
                return "Отметить можно только во время стрима, когда на нём есть зрители.";
            }
            if (message.Contains("themselves", StringComparison.OrdinalIgnoreCase)) return "Себя отметить нельзя.";
            return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
        }
        if (status == HttpStatusCode.Unauthorized) return "Twitch не разрешил: переподключи Twitch (нужно право на отметки).";
        if (status == HttpStatusCode.Forbidden)
        {
            if (!message.Contains("moderator", StringComparison.OrdinalIgnoreCase)) return "Twitch не разрешает отметить этого пользователя.";
            return otherChannel != null
                ? $"Twitch не разрешил: ты не модератор канала {otherChannel}."
                : "Twitch не разрешил: подключи Twitch аккаунтом владельца канала.";
        }
        if (status == (HttpStatusCode)429)
        {
            return message.Contains("same broadcaster", StringComparison.OrdinalIgnoreCase)
                ? "Этого уже отмечали за последний час."
                : "Отмечать можно раз в 2 минуты, подожди.";
        }
        return $"Twitch: HTTP {(int)status}.";
    }

    internal static string DescribeModerationFailure(HttpStatusCode status, string message, string? otherChannel = null)
    {
        if (status == HttpStatusCode.Forbidden && otherChannel != null) return $"Twitch не разрешил: ты не модератор канала {otherChannel}.";
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return "Twitch не разрешил: переподключи Twitch (нужно право на модерацию).";
        }
        if (status == (HttpStatusCode)429) return "Слишком часто, подожди немного.";
        if (status == HttpStatusCode.BadRequest)
        {
            if (message.Contains("already banned", StringComparison.OrdinalIgnoreCase)) return "Уже забанен.";
            if (message.Contains("not banned", StringComparison.OrdinalIgnoreCase)) return "Уже не забанен.";
            if (message.Contains("broadcaster", StringComparison.OrdinalIgnoreCase)) return "Стримера нельзя.";
            return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
        }
        return $"Twitch: HTTP {(int)status}.";
    }

    public async Task<IReadOnlyList<ModeratedChannel>> GetModeratedChannelsAsync(CancellationToken ct)
    {
        if (!Settings.HasModeratedChannelsScope)
        {
            throw new AuthRequiredException("Чтобы работать в чате канала, где ты модератор, переподключи Twitch: нужно новое право.");
        }

        var channels = new List<ModeratedChannel>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var url = $"{_endpoints.ModeratedChannelsUrl}?user_id={Uri.EscapeDataString(Settings.TwitchUserId)}&first=100";
            if (cursor != null) url += "&after=" + Uri.EscapeDataString(cursor);
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            ThrowIfUnauthorized(status, "Twitch не отдал список каналов, где ты модератор: переподключи Twitch.");
            if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
            var (items, next) = ParseModeratedChannels(body);
            channels.AddRange(items);
            if (string.IsNullOrEmpty(next) || items.Count == 0) break;
            cursor = next;
        }
        return channels
            .Where(c => c.Id != Settings.TwitchUserId)
            .GroupBy(c => c.Id)
            .Select(g => g.First())
            .OrderBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static (IReadOnlyList<ModeratedChannel> Items, string? Cursor) ParseModeratedChannels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var items = new List<ModeratedChannel>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in data.EnumerateArray())
            {
                var id = ReadString(element, "broadcaster_id");
                if (id.Length == 0) continue;
                items.Add(new ModeratedChannel(id, ReadString(element, "broadcaster_login"), ReadString(element, "broadcaster_name")));
            }
        }
        string? cursor = null;
        if (doc.RootElement.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
        {
            var value = ReadString(pagination, "cursor");
            if (value.Length > 0) cursor = value;
        }
        return (items, cursor);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct)
    {
        var urls = new List<string> { _endpoints.ChatBadgesUrl + "/global" };
        if (Settings.ChannelId.Length > 0) urls.Add(_endpoints.ChatBadgesUrl + "?broadcaster_id=" + Uri.EscapeDataString(Settings.ChannelId));

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in urls)
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            ThrowIfUnauthorized(status, "Twitch не отдал значки чата: переподключи Twitch.");
            if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
            foreach (var pair in ParseBadges(body)) map[pair.Key] = pair.Value;
        }
        return map;
    }

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

    public static IReadOnlyDictionary<string, string> ParseBadges(string json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return map;
        foreach (var set in data.EnumerateArray())
        {
            var setId = ReadString(set, "set_id");
            if (setId.Length == 0 || !set.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array) continue;
            foreach (var version in versions.EnumerateArray())
            {
                var id = ReadString(version, "id");
                var image = ReadString(version, "image_url_2x");
                if (image.Length == 0) image = ReadString(version, "image_url_1x");
                if (image.Length > 0) map[setId + "/" + id] = image;
            }
        }
        return map;
    }

    public async Task<IReadOnlySet<string>> GetManageableRewardIdsAsync(CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&only_manageable_rewards=true";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не отдал список управляемых наград: переподключи Twitch (нужно право channel:manage:redemptions).");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reward in ParseRewards(body)) ids.Add(reward.Id);
        return ids;
    }

    public async Task<RewardCreateResult> CreateRewardAsync(RewardInfo template, string title, CancellationToken ct)
    {
        RequireManageScope();
        var body = new Dictionary<string, object>
        {
            ["title"] = title,
            ["cost"] = template.Cost,
            ["is_enabled"] = template.IsEnabled,
            ["is_user_input_required"] = template.IsUserInputRequired,
            ["should_redemptions_skip_request_queue"] = template.SkipRequestQueue,
        };
        if (!string.IsNullOrWhiteSpace(template.Prompt)) body["prompt"] = template.Prompt;
        if (!string.IsNullOrWhiteSpace(template.BackgroundColor)) body["background_color"] = template.BackgroundColor;
        if (template.MaxPerStream is >= 1 and var maxPerStream)
        {
            body["is_max_per_stream_enabled"] = true;
            body["max_per_stream"] = maxPerStream;
        }
        if (template.MaxPerUserPerStream is >= 1 and var maxPerUser)
        {
            body["is_max_per_user_per_stream_enabled"] = true;
            body["max_per_user_per_stream"] = maxPerUser;
        }
        if (template.GlobalCooldownSeconds is >= 1 and var cooldown)
        {
            body["is_global_cooldown_enabled"] = true;
            body["global_cooldown_seconds"] = cooldown;
        }

        var json = JsonSerializer.Serialize(body);
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Post, url, token, json, c), ct);

        if (status == HttpStatusCode.OK)
        {
            using var doc = JsonDocument.Parse(response);
            var created = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                ? ReadString(data[0], "id")
                : "";
            return new RewardCreateResult(RewardCreateOutcome.Created, created.Length == 0 ? null : created, "");
        }

        var message = ReadMessage(response);
        if (status == HttpStatusCode.BadRequest)
        {
            if (message.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase))
            {
                return new RewardCreateResult(RewardCreateOutcome.TitleTaken, null, message);
            }
            if (message.Contains("TOO_MANY", StringComparison.OrdinalIgnoreCase))
            {
                return new RewardCreateResult(RewardCreateOutcome.LimitReached, null, message);
            }
            return new RewardCreateResult(RewardCreateOutcome.Failed, null, message);
        }
        ThrowIfUnauthorized(status, "Twitch не разрешил создавать награды: переподключи Twitch (нужно право channel:manage:redemptions; баллы канала должны быть доступны на канале).");
        return new RewardCreateResult(RewardCreateOutcome.Failed, null, $"HTTP {(int)status}. {message}".Trim());
    }

    public async Task<RedemptionUpdateResult> UpdateRedemptionAsync(
        string rewardId, string redemptionId, RedemptionDecision decision, CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}/redemptions?id={Uri.EscapeDataString(redemptionId)}" +
                  $"&broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&reward_id={Uri.EscapeDataString(rewardId)}";
        var json = JsonSerializer.Serialize(new { status = decision == RedemptionDecision.Fulfilled ? "FULFILLED" : "CANCELED" });
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Patch, url, token, json, c), ct);

        var message = ReadMessage(response);
        switch (status)
        {
            case HttpStatusCode.OK:
                return new RedemptionUpdateResult(RedemptionUpdateOutcome.Done, "");
            case HttpStatusCode.BadRequest:
                return new RedemptionUpdateResult(RedemptionUpdateOutcome.AlreadyProcessed, message);
            case HttpStatusCode.Forbidden:
                return new RedemptionUpdateResult(RedemptionUpdateOutcome.NotAllowed, message);
            case HttpStatusCode.NotFound:
                return new RedemptionUpdateResult(RedemptionUpdateOutcome.NotFound, message);
            case HttpStatusCode.Unauthorized:
                throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужно право channel:manage:redemptions).");
            default:
                return new RedemptionUpdateResult(RedemptionUpdateOutcome.Failed, $"HTTP {(int)status}. {message}".Trim());
        }
    }

    public async Task<RewardToggleResult> SetRewardEnabledAsync(string rewardId, bool enabled, CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&id={Uri.EscapeDataString(rewardId)}";
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["is_enabled"] = enabled });
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Patch, url, token, json, c), ct);

        var message = ReadMessage(response);
        return status switch
        {
            HttpStatusCode.OK => new RewardToggleResult(true, ""),
            HttpStatusCode.NotFound => new RewardToggleResult(false, "Этой награды уже нет на канале.", Missing: true),
            HttpStatusCode.Forbidden => new RewardToggleResult(false, "Twitch разрешает менять эту награду только там, где её создали."),
            HttpStatusCode.Unauthorized => throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужно право channel:manage:redemptions)."),
            (HttpStatusCode)429 => new RewardToggleResult(false, "Twitch: слишком много запросов, попробуй через минуту."),
            _ => new RewardToggleResult(false, $"Twitch: HTTP {(int)status}. {message}".Trim()),
        };
    }

    public async Task<RewardRenameResult> SetRewardTitleAsync(string rewardId, string title, CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&id={Uri.EscapeDataString(rewardId)}";
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["title"] = title });
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Patch, url, token, json, c), ct);

        var message = ReadMessage(response);
        return status switch
        {
            HttpStatusCode.OK => new RewardRenameResult(RewardRenameOutcome.Renamed, ""),
            HttpStatusCode.BadRequest when message.Contains("DUPLICATE", StringComparison.OrdinalIgnoreCase) =>
                new RewardRenameResult(RewardRenameOutcome.TitleTaken, message),
            HttpStatusCode.BadRequest => new RewardRenameResult(RewardRenameOutcome.Failed, $"Twitch не принял название: {message}".Trim()),
            HttpStatusCode.NotFound => new RewardRenameResult(RewardRenameOutcome.Failed, "Этой награды уже нет на канале."),
            HttpStatusCode.Forbidden => new RewardRenameResult(RewardRenameOutcome.Failed, "Twitch разрешает менять эту награду только там, где её создали."),
            HttpStatusCode.Unauthorized => throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужно право channel:manage:redemptions)."),
            (HttpStatusCode)429 => new RewardRenameResult(RewardRenameOutcome.Failed, "Twitch: слишком много запросов, попробуй через минуту."),
            _ => new RewardRenameResult(RewardRenameOutcome.Failed, $"Twitch: HTTP {(int)status}. {message}".Trim()),
        };
    }

    public Task<ChannelVote?> GetLatestPollAsync(CancellationToken ct) =>
        Settings.HasPollScope ? GetLatestVoteAsync(_endpoints.PollsUrl, VoteParsing.ParsePolls, ct) : Task.FromResult<ChannelVote?>(null);

    public Task<ChannelVote?> GetLatestPredictionAsync(CancellationToken ct) =>
        Settings.HasPredictionScope ? GetLatestVoteAsync(_endpoints.PredictionsUrl, VoteParsing.ParsePredictions, ct) : Task.FromResult<ChannelVote?>(null);

    public async Task<ChannelVote?> GetLastEndedAsync(VoteKind kind, CancellationToken ct)
    {
        var poll = kind == VoteKind.Poll;
        RequireVoteScope(poll ? Settings.HasPollScope : Settings.HasPredictionScope, poll ? "опросы" : "предикты");
        var url = $"{(poll ? _endpoints.PollsUrl : _endpoints.PredictionsUrl)}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&first=10";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        switch (status)
        {
            case HttpStatusCode.OK:
                var votes = poll ? VoteParsing.ParsePolls(body) : VoteParsing.ParsePredictions(body);
                return votes.FirstOrDefault(v => v.Stage == VoteStage.Ended);
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.NotFound:
                return null;
            case HttpStatusCode.Unauthorized:
                throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты).");
            default:
                throw new HttpRequestException($"Twitch: HTTP {(int)status}. {ReadMessage(body)}".Trim());
        }
    }

    public Task<VoteResult> CreatePollAsync(PollDraft draft, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPollScope, "опросы");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["title"] = draft.Title,
            ["choices"] = draft.Choices.Select(title => new Dictionary<string, string> { ["title"] = title }).ToList(),
            ["duration"] = draft.Seconds,
        };
        if (draft.PointsVoting)
        {
            body["channel_points_voting_enabled"] = true;
            body["channel_points_per_vote"] = draft.PointsPerVote;
        }
        return SendVoteAsync(HttpMethod.Post, _endpoints.PollsUrl, body, VoteParsing.ParsePolls, ct);
    }

    public Task<VoteResult> EndPollAsync(string pollId, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPollScope, "опросы");
        var body = new Dictionary<string, object> { ["broadcaster_id"] = Settings.TwitchUserId, ["id"] = pollId, ["status"] = "TERMINATED" };
        return SendVoteAsync(HttpMethod.Patch, _endpoints.PollsUrl, body, VoteParsing.ParsePolls, ct);
    }

    public Task<VoteResult> CreatePredictionAsync(PredictionDraft draft, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPredictionScope, "предикты");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["title"] = draft.Title,
            ["outcomes"] = draft.Outcomes.Select(title => new Dictionary<string, string> { ["title"] = title }).ToList(),
            ["prediction_window"] = draft.Seconds,
        };
        return SendVoteAsync(HttpMethod.Post, _endpoints.PredictionsUrl, body, VoteParsing.ParsePredictions, ct);
    }

    public Task<VoteResult> ChangePredictionAsync(string predictionId, PredictionChange change, string? winnerId, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPredictionScope, "предикты");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["id"] = predictionId,
            ["status"] = change switch
            {
                PredictionChange.Lock => "LOCKED",
                PredictionChange.Resolve => "RESOLVED",
                _ => "CANCELED",
            },
        };
        if (change == PredictionChange.Resolve) body["winning_outcome_id"] = winnerId ?? "";
        return SendVoteAsync(HttpMethod.Patch, _endpoints.PredictionsUrl, body, VoteParsing.ParsePredictions, ct);
    }

    private static void RequireVoteScope(bool granted, string what)
    {
        if (!granted) throw new AuthRequiredException($"Переподключи Twitch в настройках, вкладка «Twitch»: нужно право на {what}.");
    }

    private async Task<ChannelVote?> GetLatestVoteAsync(string baseUrl, Func<string, IReadOnlyList<ChannelVote>> parse, CancellationToken ct)
    {
        var url = $"{baseUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&first=1";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        switch (status)
        {
            case HttpStatusCode.OK:
                return parse(body).FirstOrDefault();
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.NotFound:
                return null;
            case HttpStatusCode.Unauthorized:
                throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты).");
            default:
                throw new HttpRequestException($"Twitch: HTTP {(int)status}. {ReadMessage(body)}".Trim());
        }
    }

    private async Task<VoteResult> SendVoteAsync(
        HttpMethod method, string url, Dictionary<string, object> body, Func<string, IReadOnlyList<ChannelVote>> parse, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(method, url, token, json, c), ct);
        var message = ReadMessage(response);
        return status switch
        {
            HttpStatusCode.OK => new VoteResult(true, "", parse(response).FirstOrDefault()),
            HttpStatusCode.BadRequest => new VoteResult(false, $"Twitch не принял: {message}".Trim()),
            HttpStatusCode.Forbidden => new VoteResult(false, "Twitch отказал: опросы и предикты есть только у компаньонов и партнёров Twitch."),
            HttpStatusCode.NotFound => new VoteResult(false, "Этого уже нет на канале."),
            HttpStatusCode.Unauthorized => throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты)."),
            (HttpStatusCode)429 => new VoteResult(false, "Twitch: слишком много запросов, попробуй через минуту."),
            _ => new VoteResult(false, $"Twitch: HTTP {(int)status}. {message}".Trim()),
        };
    }

    public async Task<RewardDeleteResult> DeleteRewardAsync(string rewardId, CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&id={Uri.EscapeDataString(rewardId)}";
        var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Delete, url, token, null, c), ct);

        var message = ReadMessage(body);
        return status switch
        {
            HttpStatusCode.NoContent or HttpStatusCode.OK => new RewardDeleteResult(RewardDeleteOutcome.Deleted, ""),
            HttpStatusCode.NotFound => new RewardDeleteResult(RewardDeleteOutcome.NotFound, message),
            HttpStatusCode.Forbidden => new RewardDeleteResult(RewardDeleteOutcome.NotAllowed, message),
            HttpStatusCode.Unauthorized => throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужно право channel:manage:redemptions)."),
            _ => new RewardDeleteResult(RewardDeleteOutcome.Failed, $"HTTP {(int)status}. {message}".Trim()),
        };
    }

    public async Task<DownloadedImage?> DownloadImageAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) return null;

        using var response = await _http.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode) return null;
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        if (data.Length == 0 || data.Length > 5_000_000) return null;
        return new DownloadedImage(data, response.Content.Headers.ContentType?.MediaType);
    }

    private void RequireManageScope()
    {
        if (!Settings.HasManageScope)
        {
            throw new AuthRequiredException(
                "Для управления наградами переподключи Twitch в настройках, вкладка «Twitch»: нужно право на управление наградами.");
        }
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

    public async Task CreateSubscriptionAsync(string type, string sessionId, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        var (status, body) = await PostSubscriptionAsync(type, sessionId, token, ct);
        if (status == HttpStatusCode.Unauthorized)
        {
            token = await RefreshAsync(ct, token);
            (status, body) = await PostSubscriptionAsync(type, sessionId, token, ct);
        }

        if (status is HttpStatusCode.Accepted or HttpStatusCode.Conflict) return;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthRequiredException(
                "Twitch не разрешил слушать эти события: подключи Twitch заново (нужны права channel:read:subscriptions, channel:read:redemptions и user:read:chat; для подписок канал должен быть партнёрским или аффилированным).");
        }
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        throw new HttpRequestException($"Twitch EventSub: HTTP {(int)status}. {ReadMessage(body)}".Trim());
    }

    private async Task<(HttpStatusCode, string)> PostSubscriptionAsync(string type, string sessionId, string token, CancellationToken ct)
    {
        object condition = type is EventSubParser.ChatMessageType or EventSubParser.ChatNoticeType
            ? new { broadcaster_user_id = Settings.ChannelId, user_id = Settings.TwitchUserId }
            : new { broadcaster_user_id = Settings.TwitchUserId };
        var json = JsonSerializer.Serialize(new
        {
            type,
            version = "1",
            condition,
            transport = new { method = "websocket", session_id = sessionId },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoints.EventSubSubscriptionsUrl)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
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
