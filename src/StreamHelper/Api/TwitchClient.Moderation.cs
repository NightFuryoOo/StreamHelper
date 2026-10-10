using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

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

    Task<ModerationResult> DeleteChatMessageAsync(string messageId, CancellationToken ct);
}

public sealed partial class TwitchClient
{
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

    public async Task<ModerationResult> DeleteChatMessageAsync(string messageId, CancellationToken ct)
    {
        if (!Settings.HasDeleteMessagesScope) return new ModerationResult(false, "Нужно переподключить Twitch: новое право на удаление сообщений.");
        if (string.IsNullOrWhiteSpace(messageId)) return new ModerationResult(false, "У этого сообщения нет номера, удалить его нельзя.");

        var url = $"{_endpoints.ModerationChatUrl}?broadcaster_id={Uri.EscapeDataString(Settings.ChannelId)}" +
                  $"&moderator_id={Uri.EscapeDataString(Settings.TwitchUserId)}&message_id={Uri.EscapeDataString(messageId.Trim())}";
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Delete, url, token, null, c), ct);
            if (status is HttpStatusCode.NoContent or HttpStatusCode.OK) return new ModerationResult(true, "");
            return new ModerationResult(false, DescribeDeleteFailure(status, ReadMessage(body), OtherChannel));
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

    internal static string DescribeDeleteFailure(HttpStatusCode status, string message, string? otherChannel = null)
    {
        if (status == HttpStatusCode.BadRequest)
        {
            if (message.Contains("broadcaster", StringComparison.OrdinalIgnoreCase)) return "Сообщения стримера удалять нельзя.";
            if (message.Contains("moderator", StringComparison.OrdinalIgnoreCase)) return "Сообщения других модераторов удалять нельзя.";
            return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
        }
        if (status == HttpStatusCode.NotFound) return "Сообщение уже удалено или ему больше 6 часов.";
        if (status == HttpStatusCode.Forbidden)
        {
            return otherChannel != null ? $"Twitch не разрешил: ты не модератор канала {otherChannel}." : "Twitch не разрешил: подключи Twitch аккаунтом владельца канала.";
        }
        if (status == HttpStatusCode.Unauthorized) return "Twitch не разрешил: переподключи Twitch (нужно право на удаление сообщений).";
        if (status == (HttpStatusCode)429) return "Слишком часто, подожди немного.";
        return $"Twitch: HTTP {(int)status}.";
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
}
