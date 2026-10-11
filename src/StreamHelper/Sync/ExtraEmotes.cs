using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public enum ExtraSource
{
    SevenTv,
    Bttv,
    Ffz,
}

public sealed record ExtraEmote(string Name, string Url, bool Animated, ExtraSource Source = ExtraSource.SevenTv);

public interface IExtraEmoteApi
{
    Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct);

    Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct);
}

public sealed class ExtraEmoteSources : IExtraEmoteApi
{
    private readonly IReadOnlyList<(string Label, IExtraEmoteApi Api)> _sources;

    public ExtraEmoteSources(params (string Label, IExtraEmoteApi Api)[] sources) => _sources = sources;

    public Task<IReadOnlyList<ExtraEmote>> GetGlobalAsync(CancellationToken ct) => CollectAsync("global", (api, c) => api.GetGlobalAsync(c), ct);

    public Task<IReadOnlyList<ExtraEmote>> GetChannelAsync(string twitchUserId, CancellationToken ct) =>
        CollectAsync("channel", (api, c) => api.GetChannelAsync(twitchUserId, c), ct);

    private async Task<IReadOnlyList<ExtraEmote>> CollectAsync(
        string what, Func<IExtraEmoteApi, CancellationToken, Task<IReadOnlyList<ExtraEmote>>> load, CancellationToken ct)
    {
        var all = new List<ExtraEmote>();
        var failures = new List<string>();
        foreach (var (label, api) in _sources)
        {
            try
            {
                all.AddRange(await load(api, ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                failures.Add(label + ": " + ex.Message);
                Log.Write($"{label} {what} emotes failed: " + ex.Message);
            }
        }
        if (_sources.Count > 0 && failures.Count == _sources.Count) throw new HttpRequestException(string.Join("; ", failures));
        return all;
    }
}

public sealed class ExtraEmoteCatalog
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(30);

    private readonly IExtraEmoteApi _api;
    private readonly Func<string> _channelId;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, ExtraEmote> _byName = new Dictionary<string, ExtraEmote>();
    private IReadOnlyList<ExtraEmote> _global = Array.Empty<ExtraEmote>();
    private IReadOnlyList<ExtraEmote> _channel = Array.Empty<ExtraEmote>();
    private Task? _loading;
    private DateTime _nextTryUtc;
    private DateTime _renewUtc;
    private int _generation;

    public ExtraEmoteCatalog(IExtraEmoteApi api, Func<string> channelId)
    {
        _api = api;
        _channelId = channelId;
    }

    public int Count => _byName.Count;

    public IReadOnlyList<ExtraEmote> Global => _global;

    public IReadOnlyList<ExtraEmote> Channel => _channel;

    public bool IsLoading
    {
        get
        {
            lock (_gate) return _loading is { IsCompleted: false };
        }
    }

    public bool TryGet(string word, out ExtraEmote emote) => _byName.TryGetValue(word, out emote!);

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
            _byName = new Dictionary<string, ExtraEmote>();
            _global = Array.Empty<ExtraEmote>();
            _channel = Array.Empty<ExtraEmote>();
            _loading = null;
            _nextTryUtc = default;
            _renewUtc = default;
        }
    }

    private async Task LoadAsync()
    {
        int generation;
        lock (_gate) generation = _generation;
        var global = new List<ExtraEmote>();
        var channel = new List<ExtraEmote>();
        var failed = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            global.AddRange(await _api.GetGlobalAsync(cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            failed = true;
            Log.Write("Global extension emotes failed: " + ex.Message);
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            channel.AddRange(await _api.GetChannelAsync(_channelId(), cts.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            failed = true;
            Log.Write("Channel extension emotes failed: " + ex.Message);
        }
        var map = new Dictionary<string, ExtraEmote>(StringComparer.Ordinal);
        foreach (var emote in channel) map.TryAdd(emote.Name, emote);
        foreach (var emote in global) map.TryAdd(emote.Name, emote);

        lock (_gate)
        {
            if (generation != _generation) return;
            if (map.Count > 0 && (!failed || map.Count >= _byName.Count))
            {
                _byName = map;
                _global = global;
                _channel = channel;
            }
            _nextTryUtc = DateTime.UtcNow + RetryAfter;
            _renewUtc = DateTime.UtcNow + (failed ? RetryAfter : RenewAfter);
        }
    }
}