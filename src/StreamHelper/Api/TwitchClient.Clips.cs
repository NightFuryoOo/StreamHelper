using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;

namespace StreamHelper.Api;

public sealed record ClipStart(bool Success, string Message, string Id = "", string EditUrl = "");

public interface IClipMakerApi
{
    Task<ClipStart> CreateClipAsync(CancellationToken ct);

    Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct);

    Task<ChatSendResult> SendOwnChatMessageAsync(string text, CancellationToken ct);
}

public interface IStreamClipsApi
{
    Task<IReadOnlyList<StreamClip>> GetStreamClipsAsync(string broadcasterId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);
}

public sealed partial class TwitchClient : IClipMakerApi, IStreamClipsApi
{
    private const string ClipsScopeMissing = "Чтобы делать клипы, переподключи Twitch в «Подключениях»: нужно новое право.";

    public const int ClipPagesAtMost = 10;

    public async Task<IReadOnlyList<StreamClip>> GetStreamClipsAsync(string broadcasterId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var clips = new List<StreamClip>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < ClipPagesAtMost; page++)
        {
            var url = $"{_endpoints.ClipsUrl}?broadcaster_id={Uri.EscapeDataString(broadcasterId)}" +
                $"&started_at={Uri.EscapeDataString(Rfc3339(fromUtc))}&ended_at={Uri.EscapeDataString(Rfc3339(toUtc))}&first=100" +
                (cursor != null ? "&after=" + Uri.EscapeDataString(cursor) : "");
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            ThrowIfUnauthorized(status, "Twitch не отдал клипы канала: переподключи Twitch.");
            if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
            var (items, next) = ParseStreamClips(body);
            var fresh = 0;
            foreach (var clip in items)
            {
                if (!seen.Add(clip.Id)) continue;
                clips.Add(clip);
                fresh++;
            }
            if (next.Length == 0 || fresh == 0) break;
            cursor = next;
        }
        return clips;
    }

    private static string Rfc3339(DateTime utc) =>
        (utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static (IReadOnlyList<StreamClip> Clips, string Cursor) ParseStreamClips(string json)
    {
        var clips = new List<StreamClip>();
        var cursor = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var clip in data.EnumerateArray())
                {
                    if (clip.ValueKind != JsonValueKind.Object) continue;
                    var id = ReadString(clip, "id");
                    if (id.Length == 0) continue;
                    if (!DateTime.TryParse(ReadString(clip, "created_at"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)) continue;
                    var seconds = clip.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var s) && s > 0 ? s : 0;
                    var url = ReadString(clip, "url");
                    clips.Add(new StreamClip
                    {
                        Id = id,
                        Url = url.Length > 0 ? url : "https://clips.twitch.tv/" + id,
                        Title = ReadString(clip, "title").Trim(),
                        CreatorId = ReadString(clip, "creator_id"),
                        CreatorName = ReadString(clip, "creator_name"),
                        CreatedUtc = created,
                        Seconds = seconds,
                        ThumbnailUrl = ReadString(clip, "thumbnail_url"),
                    });
                }
            }
            if (root.TryGetProperty("pagination", out var page) && page.ValueKind == JsonValueKind.Object) cursor = ReadString(page, "cursor");
        }
        catch (JsonException)
        {
        }
        return (clips, cursor);
    }

    public async Task<ClipStart> CreateClipAsync(CancellationToken ct)
    {
        if (!Settings.HasClipsScope) return new ClipStart(false, ClipsScopeMissing);
        var url = $"{_endpoints.ClipsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Post, url, token, null, c), ct);
            if (status is HttpStatusCode.Accepted or HttpStatusCode.OK)
            {
                return ParseClipStart(body) is { } started ? started : new ClipStart(false, "Twitch не сказал, какой клип он начал делать.");
            }
            return new ClipStart(false, DescribeClipFailure(status, ReadMessage(body)));
        }
        catch (AuthRequiredException ex)
        {
            return new ClipStart(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ClipStart(false, "Нет связи с Twitch: " + ex.Message);
        }
    }

    public static ClipStart? ParseClipStart(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var clip in data.EnumerateArray())
            {
                if (clip.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(clip, "id");
                if (id.Length > 0) return new ClipStart(true, "", id, ReadString(clip, "edit_url"));
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    internal static string DescribeClipFailure(HttpStatusCode status, string message)
    {
        switch ((int)status)
        {
            case 400 when message.Contains("clippable", StringComparison.OrdinalIgnoreCase):
                return "В этой категории Twitch не разрешает клипы.";
            case 400:
                return message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.";
            case 401:
                return ClipsScopeMissing;
            case 403 when message.Contains("follower", StringComparison.OrdinalIgnoreCase) || message.Contains("subscriber", StringComparison.OrdinalIgnoreCase):
                return "Клипы на канале доступны только фолловерам или подписчикам.";
            case 403 when message.Contains("enabled", StringComparison.OrdinalIgnoreCase):
                return "Клипы на канале выключены. Включи их в настройках канала на Twitch.";
            case 403:
                return "Twitch не разрешил сделать клип.";
            case 404:
                return "Клип можно сделать только во время эфира.";
            case 429:
                return "Слишком часто, подожди немного.";
            default:
                return string.Format(CultureInfo.InvariantCulture, "Twitch: HTTP {0}.", (int)status);
        }
    }
}
