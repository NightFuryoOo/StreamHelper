using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public interface IEventSubApi
{
    Task CreateSubscriptionAsync(string type, string sessionId, CancellationToken ct);
}

public sealed partial class TwitchClient
{
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
}
