using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

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

    Task<RewardToggleResult> UpdateRewardAsync(string rewardId, RewardInfo values, CancellationToken ct);

    Task<RedemptionUpdateResult> UpdateRedemptionAsync(
        string rewardId, string redemptionId, RedemptionDecision decision, CancellationToken ct);
}

public sealed partial class TwitchClient
{
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

    public async Task<RewardToggleResult> UpdateRewardAsync(string rewardId, RewardInfo values, CancellationToken ct)
    {
        RequireManageScope();
        var url = $"{_endpoints.RewardsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&id={Uri.EscapeDataString(rewardId)}";
        var body = new Dictionary<string, object>
        {
            ["cost"] = values.Cost,
            ["prompt"] = values.Prompt,
            ["is_user_input_required"] = values.IsUserInputRequired,
            ["should_redemptions_skip_request_queue"] = values.SkipRequestQueue,
            ["is_max_per_stream_enabled"] = values.MaxPerStream is >= 1,
            ["is_max_per_user_per_stream_enabled"] = values.MaxPerUserPerStream is >= 1,
            ["is_global_cooldown_enabled"] = values.GlobalCooldownSeconds is >= 1,
        };
        if (values.MaxPerStream is >= 1 and var perStream) body["max_per_stream"] = perStream;
        if (values.MaxPerUserPerStream is >= 1 and var perUser) body["max_per_user_per_stream"] = perUser;
        if (values.GlobalCooldownSeconds is >= 1 and var cooldown) body["global_cooldown_seconds"] = cooldown;
        if (!string.IsNullOrWhiteSpace(values.BackgroundColor)) body["background_color"] = values.BackgroundColor;
        var json = JsonSerializer.Serialize(body);
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Patch, url, token, json, c), ct);

        var message = ReadMessage(response);
        return status switch
        {
            HttpStatusCode.OK => new RewardToggleResult(true, ""),
            HttpStatusCode.BadRequest => new RewardToggleResult(false, message.Length > 0 ? "Twitch не принял изменения: " + message : "Twitch не принял изменения."),
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

    private void RequireManageScope()
    {
        if (!Settings.HasManageScope)
        {
            throw new AuthRequiredException(
                "Для управления наградами переподключи Twitch в настройках, вкладка «Twitch»: нужно право на управление наградами.");
        }
    }
}
