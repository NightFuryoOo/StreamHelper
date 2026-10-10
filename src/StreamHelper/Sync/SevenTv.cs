using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record SevenTvEmote(string Name, string Url, bool Animated);

public interface ISevenTvApi
{
    Task<IReadOnlyList<SevenTvEmote>> GetGlobalAsync(CancellationToken ct);

    Task<IReadOnlyList<SevenTvEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct);
}

public sealed class SevenTvClient : ISevenTvApi
{
    public const string DefaultBase = "https://7tv.io";

    private readonly HttpClient _http;
    private readonly string _base;

    public SevenTvClient(HttpClient http, string? baseUrl = null)
    {
        _http = http;
        _base = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBase : baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<SevenTvEmote>> GetGlobalAsync(CancellationToken ct)
    {
        var body = await GetAsync($"{_base}/v3/emote-sets/global", ct);
        return body == null ? Array.Empty<SevenTvEmote>() : ParseSet(body, "emotes");
    }

    public async Task<IReadOnlyList<SevenTvEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(twitchUserId)) return Array.Empty<SevenTvEmote>();
        var body = await GetAsync($"{_base}/v3/users/twitch/{Uri.EscapeDataString(twitchUserId)}", ct);
        return body == null ? Array.Empty<SevenTvEmote>() : ParseSet(body, "emote_set", "emotes");
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"7TV: HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    public static IReadOnlyList<SevenTvEmote> ParseSet(string json, params string[] path)
    {
        var emotes = new List<SevenTvEmote>();
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

    private static SevenTvEmote? ParseEmote(JsonElement item)
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
        return new SevenTvEmote(name, baseUrl + "/" + chosen, animated);
    }

    private static string Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

public sealed class SevenTvCatalog
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(30);

    private readonly ISevenTvApi _api;
    private readonly Func<string> _channelId;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, SevenTvEmote> _byName = new Dictionary<string, SevenTvEmote>();
    private Task? _loading;
    private DateTime _nextTryUtc;
    private DateTime _renewUtc;
    private int _generation;

    public SevenTvCatalog(ISevenTvApi api, Func<string> channelId)
    {
        _api = api;
        _channelId = channelId;
    }

    public int Count => _byName.Count;

    public bool IsLoading
    {
        get
        {
            lock (_gate) return _loading is { IsCompleted: false };
        }
    }

    public bool TryGet(string word, out SevenTvEmote emote) => _byName.TryGetValue(word, out emote!);

    public Task EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false }) return _loading;
            var due = _byName.Count == 0 ? DateTime.UtcNow >= _nextTryUtc : DateTime.UtcNow >= _renewUtc;
            if (!due) return Task.CompletedTask;
            _loading = LoadAsync();
            return _loading;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _byName = new Dictionary<string, SevenTvEmote>();
            _loading = null;
            _nextTryUtc = default;
            _renewUtc = default;
        }
    }

    private async Task LoadAsync()
    {
        int generation;
        lock (_gate) generation = _generation;
        var map = new Dictionary<string, SevenTvEmote>(StringComparer.Ordinal);
        var failed = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var emote in await _api.GetGlobalAsync(cts.Token)) map[emote.Name] = emote;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            failed = true;
            Log.Write("7TV global emotes failed: " + ex.Message);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var emote in await _api.GetChannelAsync(_channelId(), cts.Token)) map[emote.Name] = emote;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            failed = true;
            Log.Write("7TV channel emotes failed: " + ex.Message);
        }

        lock (_gate)
        {
            if (generation != _generation) return;
            if (map.Count > 0 && (!failed || map.Count >= _byName.Count)) _byName = map;
            _nextTryUtc = DateTime.UtcNow + RetryAfter;
            _renewUtc = DateTime.UtcNow + (failed ? RetryAfter : RenewAfter);
        }
    }
}