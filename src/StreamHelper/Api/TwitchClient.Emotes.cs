using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public sealed record TwitchEmote(string Id, string Name, bool Animated, string Type = "", string OwnerId = "");

public sealed record TwitchUser(string Id, string Login, string DisplayName, string ProfileImageUrl);

public interface IEmoteApi
{
    Task<IReadOnlyList<TwitchEmote>> GetGlobalEmotesAsync(CancellationToken ct);
    Task<IReadOnlyList<TwitchEmote>> GetChannelEmotesAsync(string broadcasterId, CancellationToken ct);
    Task<IReadOnlyList<TwitchEmote>> GetUserEmotesAsync(string? broadcasterId, CancellationToken ct);
    Task<IReadOnlyList<TwitchUser>> GetUsersAsync(IReadOnlyList<string> ids, CancellationToken ct);
}

public sealed partial class TwitchClient
{
    private const int MaxEmotePages = 50;

    public async Task<IReadOnlyList<TwitchEmote>> GetGlobalEmotesAsync(CancellationToken ct) =>
        (await GetEmotePageAsync(_endpoints.ChatEmotesUrl + "/global", ct)).Emotes;

    public async Task<IReadOnlyList<TwitchEmote>> GetChannelEmotesAsync(string broadcasterId, CancellationToken ct) =>
        (await GetEmotePageAsync(_endpoints.ChatEmotesUrl + "?broadcaster_id=" + Uri.EscapeDataString(broadcasterId), ct)).Emotes;

    public async Task<IReadOnlyList<TwitchEmote>> GetUserEmotesAsync(string? broadcasterId, CancellationToken ct)
    {
        if (!Settings.HasEmotesScope) throw new AuthRequiredException("Для своих смайлов переподключи Twitch в настройках, раздел «Подключения»: нужно новое право.");
        var all = new List<TwitchEmote>();
        string? cursor = null;
        for (var page = 0; page < MaxEmotePages; page++)
        {
            var url = _endpoints.ChatEmotesUrl + "/user?user_id=" + Uri.EscapeDataString(Settings.TwitchUserId);
            if (!string.IsNullOrEmpty(broadcasterId)) url += "&broadcaster_id=" + Uri.EscapeDataString(broadcasterId);
            if (cursor != null) url += "&after=" + Uri.EscapeDataString(cursor);
            var (emotes, next) = await GetEmotePageAsync(url, ct);
            all.AddRange(emotes);
            if (string.IsNullOrEmpty(next) || next == cursor) break;
            cursor = next;
        }
        return all;
    }

    public async Task<IReadOnlyList<TwitchUser>> GetUsersAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var users = new List<TwitchUser>();
        for (var i = 0; i < ids.Count; i += 100)
        {
            var batch = ids.Skip(i).Take(100).Where(id => id.Length > 0).ToList();
            if (batch.Count == 0) continue;
            var url = _endpoints.UsersUrl + "?" + string.Join("&", batch.Select(id => "id=" + Uri.EscapeDataString(id)));
            var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
            ThrowIfUnauthorized(status, "Twitch не отдал каналы: переподключи Twitch.");
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
            users.AddRange(ParseUsers(body));
        }
        return users;
    }

    public async Task<TwitchUser?> GetUserByLoginAsync(string login, CancellationToken ct)
    {
        var url = _endpoints.UsersUrl + "?login=" + Uri.EscapeDataString(login);
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не отдал канал: переподключи Twitch.");
        if (status == HttpStatusCode.BadRequest) return null;
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseUsers(body).FirstOrDefault(u => string.Equals(u.Login, login, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<TwitchUser> ParseUsers(string json)
    {
        var users = new List<TwitchUser>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return users;
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "id");
            if (id.Length > 0) users.Add(new TwitchUser(id, ReadString(item, "login"), ReadString(item, "display_name"), ReadString(item, "profile_image_url")));
        }
        return users;
    }

    private async Task<(IReadOnlyList<TwitchEmote> Emotes, string? Cursor)> GetEmotePageAsync(string url, CancellationToken ct)
    {
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        ThrowIfUnauthorized(status, "Twitch не отдал смайлы: переподключи Twitch.");
        if (status == (HttpStatusCode)429) throw new HttpRequestException("Twitch: слишком много запросов (HTTP 429).");
        if (status != HttpStatusCode.OK) throw new HttpRequestException($"Twitch: HTTP {(int)status}.");
        return ParseEmotes(body);
    }

    public static (IReadOnlyList<TwitchEmote> Emotes, string? Cursor) ParseEmotes(string json)
    {
        var emotes = new List<TwitchEmote>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = ReadString(item, "id");
                var name = ReadString(item, "name");
                if (id.Length == 0 || name.Length == 0) continue;
                var animated = false;
                if (item.TryGetProperty("format", out var formats) && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var format in formats.EnumerateArray())
                    {
                        if (format.ValueKind == JsonValueKind.String && format.GetString() == "animated") animated = true;
                    }
                }
                emotes.Add(new TwitchEmote(id, name, animated, ReadString(item, "emote_type"), ReadString(item, "owner_id")));
            }
        }
        string? cursor = null;
        if (root.TryGetProperty("pagination", out var pagination) && pagination.ValueKind == JsonValueKind.Object)
        {
            var value = ReadString(pagination, "cursor");
            if (value.Length > 0) cursor = value;
        }
        return (emotes, cursor);
    }
}
