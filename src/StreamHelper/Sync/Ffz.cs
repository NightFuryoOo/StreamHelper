using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Sync;

public sealed class FfzClient : IExtraEmoteApi
{
    public const string DefaultBase = "https://api.frankerfacez.com";

    private readonly HttpClient _http;
    private readonly string _base;

    public FfzClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _base = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct)
    {
        var body = await GetAsync($"{_base}/v1/set/global", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseGlobal(body);
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(twitchUserId)) return Array.Empty<ExtraEmote>();
        var body = await GetAsync($"{_base}/v1/room/id/{Uri.EscapeDataString(twitchUserId)}", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseRoom(body);
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"FFZ: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<ExtraEmote> ParseGlobal(string json)
    {
        var emotes = new List<ExtraEmote>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sets", out var sets) || sets.ValueKind != JsonValueKind.Object) return emotes;
        if (!root.TryGetProperty("default_sets", out var defaults) || defaults.ValueKind != JsonValueKind.Array) return emotes;
        foreach (var id in defaults.EnumerateArray())
        {
            var key = id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
            if (key.Length > 0 && sets.TryGetProperty(key, out var set)) CollectSet(set, emotes);
        }
        return emotes;
    }

    public static IReadOnlyList<ExtraEmote> ParseRoom(string json)
    {
        var emotes = new List<ExtraEmote>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sets", out var sets) || sets.ValueKind != JsonValueKind.Object) return emotes;
        foreach (var set in sets.EnumerateObject()) CollectSet(set.Value, emotes);
        return emotes;
    }

    private static void CollectSet(JsonElement set, List<ExtraEmote> emotes)
    {
        if (set.ValueKind != JsonValueKind.Object || !set.TryGetProperty("emoticons", out var list) || list.ValueKind != JsonValueKind.Array) return;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (name.Length == 0 || !item.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Object) continue;
            var url = Url(urls, "2") ?? Url(urls, "1") ?? Url(urls, "4");
            if (url != null) emotes.Add(new ExtraEmote(name, url, false, ExtraSource.Ffz));
        }
    }

    private static string? Url(JsonElement urls, string size)
    {
        if (!urls.TryGetProperty(size, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var url = value.GetString() ?? "";
        if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : null;
    }
}