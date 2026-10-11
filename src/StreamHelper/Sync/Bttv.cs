using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Sync;

public sealed class BttvClient : IExtraEmoteApi
{
    public const string DefaultApi = "https://api.betterttv.net";
    public const string DefaultCdn = "https://cdn.betterttv.net";

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly string _cdn;

    public BttvClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _api = DefaultApi;
            _cdn = DefaultCdn;
        }
        else
        {
            _api = baseUrl.TrimEnd('/');
            _cdn = _api + "/cdn";
        }
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct)
    {
        var body = await GetAsync($"{_api}/3/cached/emotes/global", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseGlobal(body, _cdn);
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(twitchUserId)) return Array.Empty<ExtraEmote>();
        var body = await GetAsync($"{_api}/3/cached/users/twitch/{Uri.EscapeDataString(twitchUserId)}", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseChannel(body, _cdn);
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"BTTV: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<ExtraEmote> ParseGlobal(string json, string cdn = DefaultCdn)
    {
        var emotes = new List<ExtraEmote>();
        using var doc = JsonDocument.Parse(json);
        Collect(doc.RootElement, cdn, emotes);
        return emotes;
    }

    public static IReadOnlyList<ExtraEmote> ParseChannel(string json, string cdn = DefaultCdn)
    {
        var emotes = new List<ExtraEmote>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return emotes;
        foreach (var list in new[] { "channelEmotes", "sharedEmotes" })
        {
            if (doc.RootElement.TryGetProperty(list, out var items)) Collect(items, cdn, emotes);
        }
        return emotes;
    }

    private static void Collect(JsonElement items, string cdn, List<ExtraEmote> emotes)
    {
        if (items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var id = Text(item, "id");
            var code = Text(item, "code");
            if (id.Length == 0 || code.Length == 0) continue;
            if (item.TryGetProperty("modifier", out var modifier) && modifier.ValueKind == JsonValueKind.True) continue;
            var animated = item.TryGetProperty("animated", out var flag) && flag.ValueKind == JsonValueKind.True ||
                           Text(item, "imageType").Equals("gif", StringComparison.OrdinalIgnoreCase);
            emotes.Add(new ExtraEmote(code, $"{cdn.TrimEnd('/')}/emote/{Uri.EscapeDataString(id)}/2x.{(animated ? "gif" : "png")}", animated, ExtraSource.Bttv));
        }
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}