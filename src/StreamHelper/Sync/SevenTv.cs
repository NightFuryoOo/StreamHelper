using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Sync;

public sealed class SevenTvClient : IExtraEmoteApi
{
    public const string DefaultBase = "https://7tv.io";

    private readonly HttpClient _http;
    private readonly string _base;

    public SevenTvClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _base = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct)
    {
        var body = await GetAsync($"{_base}/v3/emote-sets/global", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseSet(body, "emotes");
    }

    public async Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(twitchUserId)) return Array.Empty<ExtraEmote>();
        var body = await GetAsync($"{_base}/v3/users/twitch/{Uri.EscapeDataString(twitchUserId)}", ct);
        return body == null ? Array.Empty<ExtraEmote>() : ParseSet(body, "emote_set", "emotes");
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"7TV: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<ExtraEmote> ParseSet(string json, params string[] path)
    {
        var emotes = new List<ExtraEmote>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var node = doc.RootElement;
            foreach (var step in path)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out node)) return emotes;
            }
            if (node.ValueKind != JsonValueKind.Array) return emotes;
            foreach (var item in node.EnumerateArray())
            {
                var emote = ParseEmote(item);
                if (emote != null) emotes.Add(emote);
            }
        }
        catch (JsonException)
        {
        }
        return emotes;
    }

    private static ExtraEmote? ParseEmote(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var name = Text(item, "name");
        if (!item.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        if (name.Length == 0) name = Text(data, "name");
        if (name.Length == 0 || !data.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object) return null;

        var baseUrl = Text(host, "url");
        if (baseUrl.Length == 0) return null;
        if (baseUrl.StartsWith("//", StringComparison.Ordinal)) baseUrl = "https:" + baseUrl;
        baseUrl = baseUrl.TrimEnd('/');

        var animated = data.TryGetProperty("animated", out var flag) && flag.ValueKind == JsonValueKind.True;
        var extension = animated ? ".gif" : ".png";
        var files = new List<string>();
        if (host.TryGetProperty("files", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in list.EnumerateArray()) files.Add(Text(file, "name"));
        }
        var chosen = files.FirstOrDefault(f => f.Equals("2x" + extension, StringComparison.OrdinalIgnoreCase))
                     ?? files.FirstOrDefault(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        if (chosen == null) return null;
        return new ExtraEmote(name, baseUrl + "/" + chosen, animated);
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
