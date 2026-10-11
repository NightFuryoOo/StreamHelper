using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class EmoteLibrary
{
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly IEmoteApi _api;
    private readonly ExtraEmoteCatalog _extra;
    private readonly SettingsStore _settings;
    private readonly TwitchEndpoints _endpoints;
    private IReadOnlyList<EmoteChoice> _all = Array.Empty<EmoteChoice>();
    private IReadOnlyDictionary<string, EmoteOwner> _owners = new Dictionary<string, EmoteOwner>();
    private Dictionary<string, string> _urls = new(StringComparer.Ordinal);
    private string _key = "";
    private DateTime _loadedUtc;
    private Task? _loading;
    private int _generation;

    public EmoteLibrary(IEmoteApi api, ExtraEmoteCatalog extra, SettingsStore settings, TwitchEndpoints endpoints)
    {
        _api = api;
        _extra = extra;
        _settings = settings;
        _endpoints = endpoints;
    }

    public IReadOnlyList<EmoteChoice> All => _all;

    public IReadOnlyDictionary<string, EmoteOwner> Owners => _owners;

    public bool IsLoading => _loading is { IsCompleted: false };

    public string? UrlFor(string name) => _urls.TryGetValue(name, out var url) ? url : null;

    private string KeyNow
    {
        get
        {
            var settings = _settings.Current;
            return settings.ChannelId + "|" + settings.HasEmotesScope + "|" + settings.HasTwitchTokens;
        }
    }

    public Task EnsureLoadedAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        if (_key == KeyNow && DateTime.UtcNow - _loadedUtc < RenewAfter) return Task.CompletedTask;
        _loading = LoadAsync();
        return _loading;
    }

    public void Reset()
    {
        _generation++;
        _all = Array.Empty<EmoteChoice>();
        _owners = new Dictionary<string, EmoteOwner>();
        _urls = new Dictionary<string, string>(StringComparer.Ordinal);
        _key = "";
        _loading = null;
    }

    private async Task LoadAsync()
    {
        var generation = _generation;
        var key = KeyNow;
        var settings = _settings.Current;
        var channelId = settings.ChannelId;
        IReadOnlyList<TwitchEmote>? mine = null;
        IReadOnlyList<TwitchEmote> channel = Array.Empty<TwitchEmote>();
        IReadOnlyList<TwitchEmote> global = Array.Empty<TwitchEmote>();
        if (settings.HasTwitchTokens)
        {
            if (settings.HasEmotesScope) mine = await TryAsync("user", ct => _api.GetUserEmotesAsync(channelId, ct));
            if (mine == null && channelId.Length > 0) channel = await TryAsync("channel", ct => _api.GetChannelEmotesAsync(channelId, ct)) ?? channel;
            global = await TryAsync("global", _api.GetGlobalEmotesAsync) ?? global;
        }
        await Task.WhenAny(_extra.EnsureLoadedAsync(), Task.Delay(Wait));
        if (generation != _generation) return;
        var all = EmoteChoices.Collect(mine, channel, global, _extra.Channel, _extra.Global, channelId, e => _endpoints.EmoteUrl(e.Id, e.Animated));
        var owners = new Dictionary<string, EmoteOwner>(StringComparer.Ordinal);
        var ids = EmoteChoices.Owners(all);
        if (ids.Count > 0 && settings.HasTwitchTokens)
        {
            try
            {
                using var cts = new CancellationTokenSource(Wait);
                foreach (var user in await _api.GetUsersAsync(ids, cts.Token))
                {
                    owners[user.Id] = new EmoteOwner(user.DisplayName.Length > 0 ? user.DisplayName : user.Login, user.ProfileImageUrl);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or AuthRequiredException or System.Text.Json.JsonException)
            {
                Log.Write("Twitch emote owners failed: " + ex.Message);
            }
        }
        if (generation != _generation) return;
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var emote in all) urls.TryAdd(emote.Name, emote.Url);
        _all = all;
        _urls = urls;
        _owners = owners;
        _key = key;
        _loadedUtc = DateTime.UtcNow;
    }

    private static async Task<IReadOnlyList<TwitchEmote>?> TryAsync(string what, Func<CancellationToken, Task<IReadOnlyList<TwitchEmote>>> load)
    {
        try
        {
            using var cts = new CancellationTokenSource(Wait);
            return await load(cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or AuthRequiredException or System.Text.Json.JsonException)
        {
            Log.Write($"Twitch {what} emotes failed: " + ex.Message);
            return null;
        }
    }
}
