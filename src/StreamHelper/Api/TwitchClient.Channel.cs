using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public sealed record ChannelInfo(string Title, string GameId, string GameName);

public sealed record StreamCategory(string Id, string Name, string BoxArtUrl);

public sealed record ChannelUpdateResult(bool Success, string Message);

public interface IChannelInfoApi
{
    Task<ChannelInfo?> GetChannelInfoAsync(CancellationToken ct);

    Task<ChannelUpdateResult> UpdateChannelInfoAsync(string title, string gameId, CancellationToken ct);

    Task<IReadOnlyList<StreamCategory>> SearchCategoriesAsync(string query, CancellationToken ct);
}

public sealed partial class TwitchClient : IChannelInfoApi
{
    public const int MaxStreamTitle = 140;
    private const string BroadcastScopeMissing = "Чтобы менять название стрима, переподключи Twitch в «Подключениях»: нужно новое право.";

    public async Task<ChannelInfo?> GetChannelInfoAsync(CancellationToken ct)
    {
        var url = $"{_endpoints.ChannelsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не отдал данные канала: переподключи Twitch.");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseChannelInfo(body);
    }

    public async Task<ChannelUpdateResult> UpdateChannelInfoAsync(string title, string gameId, CancellationToken ct)
    {
        if (!Settings.HasBroadcastScope) return new ChannelUpdateResult(false, BroadcastScopeMissing);
        var clean = title.Trim();
        if (clean.Length == 0) return new ChannelUpdateResult(false, "Название не может быть пустым.");
        if (clean.Length > MaxStreamTitle) return new ChannelUpdateResult(false, $"Название длиннее {MaxStreamTitle} символов.");
        var url = $"{_endpoints.ChannelsUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}";
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["title"] = clean, ["game_id"] = gameId });
        try
        {
            var (status, body) = await CallWithRefreshAsync((token, c) => SendJsonAsync(HttpMethod.Patch, url, token, json, c), ct);
            if (status is HttpStatusCode.NoContent or HttpStatusCode.OK) return new ChannelUpdateResult(true, "");
            return new ChannelUpdateResult(false, DescribeChannelUpdateFailure(status, ReadMessage(body)));
        }
        catch (AuthRequiredException ex)
        {
            return new ChannelUpdateResult(false, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return new ChannelUpdateResult(false, "Нет связи с Twitch: " + ex.Message);
        }
    }

    internal static string DescribeChannelUpdateFailure(HttpStatusCode status, string message) => (int)status switch
    {
        400 when message.Contains("game", StringComparison.OrdinalIgnoreCase) => "Twitch не нашёл такую категорию, выбери её из списка.",
        400 => message.Length > 0 ? "Twitch отказал: " + message : "Twitch отказал.",
        401 => BroadcastScopeMissing,
        403 => "Twitch не разрешил: менять название может только владелец канала.",
        429 => "Слишком часто, подожди немного.",
        _ when message.Length > 0 => $"Twitch отказал (HTTP {(int)status}): {message}",
        _ => $"Twitch: HTTP {(int)status}.",
    };

    public async Task<IReadOnlyList<StreamCategory>> SearchCategoriesAsync(string query, CancellationToken ct)
    {
        var text = query.Trim();
        if (text.Length == 0) return Array.Empty<StreamCategory>();
        var url = $"{_endpoints.SearchCategoriesUrl}?query={Uri.EscapeDataString(text)}&first=10";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не дал искать категории: переподключи Twitch.");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseCategories(body);
    }

    public static ChannelInfo? ParseChannelInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            foreach (var channel in data.EnumerateArray())
            {
                if (channel.ValueKind != JsonValueKind.Object) continue;
                return new ChannelInfo(ReadString(channel, "title"), ReadString(channel, "game_id"), ReadString(channel, "game_name"));
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static IReadOnlyList<StreamCategory> ParseCategories(string json)
    {
        var list = new List<StreamCategory>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = ReadString(item, "id");
                var name = ReadString(item, "name");
                if (id.Length == 0 || name.Length == 0) continue;
                list.Add(new StreamCategory(id, name, BoxArt(ReadString(item, "box_art_url"))));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    public static string BoxArt(string url) =>
        url.Replace("{width}", "52", StringComparison.Ordinal).Replace("{height}", "72", StringComparison.Ordinal);
}
