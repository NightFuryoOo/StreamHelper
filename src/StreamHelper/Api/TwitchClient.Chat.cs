using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public sealed record ChatSendResult(bool Success, string Message);

public interface IChatSendApi
{
    Task<ChatSendResult> SendChatMessageAsync(string text, string? replyToMessageId, CancellationToken ct);
}

public interface IBadgeApi
{
    Task<IReadOnlyDictionary<string, string>> GetBadgeImagesAsync(CancellationToken ct);
}

public sealed partial class TwitchClient
{
    public async Task<ChatSendResult> SendChatMessageAsync(string text, string? replyToMessageId, CancellationToken ct)
    {
        if (!Settings.HasChatWriteScope) return new ChatSendResult(false, "Чтобы писать в чат, переподключи Twitch во вкладке «Twitch»: нужно новое право.");
        var message = text.Trim();
        if (message.Length == 0) return new ChatSendResult(false, "");
        if (message.Length > MaxChatMessage) return new ChatSendResult(false, $"Слишком длинное сообщение: Twitch принимает до {MaxChatMessage} знаков.");

        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.ChannelId,
            ["sender_id"] = Settings.TwitchUserId,
            ["message"] = message,
        };
        if (!string.IsNullOrEmpty(replyToMessageId)) body["reply_parent_message_id"] = replyToMessageId;
        var json = JsonSerializer.Serialize(body);
        try
        {
            var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Post, _endpoints.ChatMessagesUrl, token, json, c), ct);
            return status == HttpStatusCode.OK ? ParseChatSend(response) : new ChatSendResult(false, DescribeChatSendFailure(status, ReadMessage(response)));
        }
        catch (AuthRequiredException ex)
        {
            return new ChatSendResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ChatSendResult(false, "Нет связи с Twitch: " + ex.Message);
        }
    }

    public static ChatSendResult ParseChatSend(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            {
                return new ChatSendResult(false, "Twitch не ответил, ушло ли сообщение.");
            }
            var first = data[0];
            if (ReadBool(first, "is_sent")) return new ChatSendResult(true, "");
            var code = "";
            var text = "";
            if (first.TryGetProperty("drop_reason", out var drop) && drop.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(drop, "code");
                text = ReadString(drop, "message");
            }
            return new ChatSendResult(false, DescribeDrop(code, text));
        }
        catch (JsonException)
        {
            return new ChatSendResult(false, "Twitch ответил непонятно, сообщение, скорее всего, не ушло.");
        }
    }

    internal static string DescribeDrop(string code, string message) => code switch
    {
        "msg_duplicate" => "Twitch не пропустил: такое же сообщение уже было только что.",
        "msg_ratelimit" => "Слишком часто: подожди немного.",
        "msg_slowmode" => "В чате медленный режим: подожди немного.",
        "msg_subsonly" => "Чат только для подписчиков.",
        "msg_followersonly" or "msg_followersonly_zero" or "msg_followersonly_followed" => "Чат только для фолловеров.",
        "msg_emoteonly" => "В чате можно писать только смайлами.",
        "msg_r9k" => "В чате включены уникальные сообщения: такое уже было.",
        "msg_banned" => "Тебя забанили в этом чате.",
        "msg_timedout" => "У тебя тайм-аут в этом чате.",
        "msg_channel_suspended" => "Канал заблокирован.",
        "msg_verified_email" => "Twitch просит подтвердить почту, чтобы писать в этот чат.",
        "msg_requires_verified_phone_number" => "Twitch просит подтвердить номер телефона, чтобы писать в этот чат.",
        _ => message.Length > 0 ? "Twitch не пропустил: " + message : "Twitch не пропустил сообщение.",
    };

    internal static string DescribeChatSendFailure(HttpStatusCode status, string message)
    {
        if (status == HttpStatusCode.Unauthorized) return "Twitch не разрешил: переподключи Twitch (нужно право писать в чат).";
        if (status == HttpStatusCode.Forbidden) return "Twitch не разрешил писать в этот чат.";
        if (status == (HttpStatusCode)422) return $"Слишком длинное сообщение: Twitch принимает до {MaxChatMessage} знаков.";
        if (status == (HttpStatusCode)429) return "Слишком часто: подожди немного.";
        if (status == HttpStatusCode.BadRequest) return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
        return $"Twitch: HTTP {(int)status}.";
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
}
