using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class ChatterDirectory
{
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private readonly IChattersApi _api;
    private readonly Func<bool> _allowed;
    private IReadOnlyList<Chatter> _all = Array.Empty<Chatter>();
    private DateTime _nextUtc;
    private Task? _loading;
    private int _generation;

    public ChatterDirectory(IChattersApi api, Func<bool> allowed)
    {
        _api = api;
        _allowed = allowed;
    }

    public IReadOnlyList<Chatter> All => _all;

    public Task RefreshAsync()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        if (!_allowed() || DateTime.UtcNow < _nextUtc) return Task.CompletedTask;
        _loading = LoadAsync();
        return _loading;
    }

    public void Reset()
    {
        _generation++;
        _all = Array.Empty<Chatter>();
        _nextUtc = default;
        _loading = null;
    }

    private async Task LoadAsync()
    {
        var generation = _generation;
        try
        {
            using var cts = new CancellationTokenSource(Wait);
            var chatters = await _api.GetChattersAsync(cts.Token);
            if (generation != _generation) return;
            _all = chatters.Select(c => new Chatter(c.Login, c.Name.Length > 0 ? c.Name : c.Login, "")).ToList();
            _nextUtc = DateTime.UtcNow + RenewAfter;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or AuthRequiredException or System.Text.Json.JsonException)
        {
            Log.Write("Twitch chatters failed: " + ex.Message);
            if (generation == _generation) _nextUtc = DateTime.UtcNow + RetryAfter;
        }
    }
}