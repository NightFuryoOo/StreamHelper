using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class ChatViewers
{
    private static readonly TimeSpan KeepOnError = TimeSpan.FromMinutes(5);

    private readonly ILiveStreamApi _api;
    private readonly Func<AppSettings> _settings;
    private readonly Func<DateTime> _now;
    private bool _loading;
    private bool _again;
    private DateTime _seenUtc;

    public ChatViewers(ILiveStreamApi api, Func<AppSettings> settings, Func<DateTime>? now = null)
    {
        _api = api;
        _settings = settings;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public LiveStream? Stream { get; private set; }

    public event Action? Changed;

    public bool Wanted
    {
        get
        {
            var settings = _settings();
            return settings.ChatShowViewers && settings.HasTwitchTokens && settings.ChannelId.Length > 0;
        }
    }

    public void Clear() => Set(null);

    public async Task RefreshAsync()
    {
        if (_loading)
        {
            _again = true;
            return;
        }
        if (!Wanted)
        {
            Clear();
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _again = false;
                var channel = _settings().ChannelId;
                LiveStream? found;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var streams = await _api.GetLiveStreamsAsync(new[] { channel }, cts.Token);
                    found = streams.FirstOrDefault(s => s.UserId == channel);
                }
                catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException or JsonException)
                {
                    Log.Write("Viewer count failed: " + ex.Message);
                    if (_now() - _seenUtc > KeepOnError) Clear();
                    continue;
                }
                if (!Wanted || _settings().ChannelId != channel)
                {
                    Clear();
                    _again = Wanted;
                    continue;
                }
                _seenUtc = _now();
                Set(found);
            }
            while (_again);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Set(LiveStream? stream)
    {
        if (Equals(Stream, stream)) return;
        Stream = stream;
        Changed?.Invoke();
    }
}
