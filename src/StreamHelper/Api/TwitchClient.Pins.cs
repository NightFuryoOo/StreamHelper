using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public sealed record PinnedMessage(ChatMessage Message, string PinnedByLogin, string PinnedByName, DateTime StartsUtc, DateTime? EndsUtc)
{
    public string PinnedByLabel => PinnedByName.Length > 0 ? PinnedByName : PinnedByLogin;
}

public sealed record PinCheck(bool Success, PinnedMessage? Pinned, string Error = "");

public sealed partial class TwitchClient
{
    private const string PinScopeMissing = "Нужно переподключить Twitch: нужно право на удаление и закрепление сообщений.";

    private string PinsUrl() =>
        $"{_endpoints.ChatPinsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.ChannelId)}&moderator_id={Uri.EscapeDataString(Settings.TwitchUserId)}";

    public async Task<PinCheck> GetPinnedAsync(CancellationToken ct)
    {
        if (!Settings.HasDeleteMessagesScope) return new PinCheck(false, null, PinScopeMissing);
        var url = PinsUrl();
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            if (status == HttpStatusCode.OK) return new PinCheck(true, ParsePinned(body));
            return new PinCheck(false, null, DescribePinFailure(status, ReadMessage(body), OtherChannel));
        }
        catch (AuthRequiredException ex)
        {
            return new PinCheck(false, null, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new PinCheck(false, null, "Нет связи с Twitch: " + ex.Message);
        }
    }

    public Task<ModerationResult> PinAsync(string messageId, int? durationSeconds, CancellationToken ct)
    {
        var query = "&message_id=" + Uri.EscapeDataString(messageId.Trim());
        if (durationSeconds is { } seconds) query += "&duration_seconds=" + seconds.ToString(CultureInfo.InvariantCulture);
        return ChangePinAsync(HttpMethod.Put, messageId, query, ct);
    }

    public Task<ModerationResult> UnpinAsync(string messageId, CancellationToken ct) =>
        ChangePinAsync(HttpMethod.Delete, messageId, "&message_id=" + Uri.EscapeDataString(messageId.Trim()), ct);

    private async Task<ModerationResult> ChangePinAsync(HttpMethod method, string messageId, string query, CancellationToken ct)
    {
        if (!Settings.HasDeleteMessagesScope) return new ModerationResult(false, PinScopeMissing);
        if (string.IsNullOrWhiteSpace(messageId)) return new ModerationResult(false, "У этого сообщения нет номера, закрепить его нельзя.");
        var url = PinsUrl() + query;
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(method, url, token, null, c), ct);
            if (status is HttpStatusCode.NoContent or HttpStatusCode.OK) return new ModerationResult(true, "");
            if (status == HttpStatusCode.NotFound && method == HttpMethod.Delete) return new ModerationResult(true, "");
            return new ModerationResult(false, DescribePinFailure(status, ReadMessage(body), OtherChannel));
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

    internal static string DescribePinFailure(HttpStatusCode status, string message, string? otherChannel)
    {
        switch ((int)status)
        {
            case 400 when message.Contains("duration", StringComparison.OrdinalIgnoreCase):
                return "Twitch не принял срок закрепления.";
            case 400:
                return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
            case 401:
                return "Twitch не разрешил: переподключи Twitch (нужно право на удаление и закрепление сообщений).";
            case 403:
                return otherChannel != null
                    ? $"Twitch не разрешил: ты не модератор канала {otherChannel}."
                    : "Twitch не разрешил закреплять сообщения на этом канале.";
            case 404:
                return "Twitch не нашёл это сообщение: возможно, его удалили.";
            case 409:
                return "Это сообщение уже закреплено.";
            case 429:
                return "Слишком часто, подожди немного.";
            default:
                return $"Twitch: HTTP {(int)status}.";
        }
    }

    public static PinnedMessage? ParsePinned(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var pin in data.EnumerateArray())
            {
                if (pin.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(pin, "message_id");
                if (id.Length == 0) continue;
                var (text, mentions, parts) = EventSubParser.ReadBody(pin);
                var starts = ReadTimestamp(pin, "starts_at");
                var ends = ReadTimestamp(pin, "ends_at");
                var message = new ChatMessage(id, ReadString(pin, "sender_user_id"), ReadString(pin, "sender_user_login"), ReadString(pin, "sender_user_name"),
                    text, mentions, starts == default ? DateTime.UtcNow : starts, Fragments: parts.Count > 0 ? parts : null);
                return new PinnedMessage(message, ReadString(pin, "pinned_by_user_login"), ReadString(pin, "pinned_by_user_name"),
                    starts, ends == default ? null : ends);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }
}
