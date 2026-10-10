using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class ChatFeed
{
    public const int KeepRecent = 60;
    private const int RememberedIds = 500;

    public const int KeepHistory = 20000;

    private readonly object _gate = new();
    private readonly Queue<ChatMessage> _recent = new();
    private readonly Queue<ChatMessage> _history = new();
    private readonly Queue<string> _idOrder = new();
    private readonly HashSet<string> _ids = new();

    public event Action<ChatMessage>? Message;

    public void Push(ChatMessage message)
    {
        lock (_gate)
        {
            if (message.MessageId.Length > 0)
            {
                if (!_ids.Add(message.MessageId)) return;
                _idOrder.Enqueue(message.MessageId);
                while (_idOrder.Count > RememberedIds) _ids.Remove(_idOrder.Dequeue());
            }
            _recent.Enqueue(message);
            while (_recent.Count > KeepRecent) _recent.Dequeue();
            _history.Enqueue(message);
            while (_history.Count > KeepHistory) _history.Dequeue();
        }
        Message?.Invoke(message);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _recent.Clear();
            _history.Clear();
            _idOrder.Clear();
            _ids.Clear();
        }
    }

    public IReadOnlyList<ChatMessage> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    public static string KeyOf(ChatMessage message) =>
        message.ChatterId.Length > 0 ? message.ChatterId : message.ChatterLogin.ToLowerInvariant();

    public IReadOnlyList<ChatMessage> HistoryOf(string chatterKey, DateTime? sinceUtc = null)
    {
        lock (_gate)
        {
            return _history
                .Where(m => KeyOf(m) == chatterKey && (sinceUtc == null || m.AtUtc >= sinceUtc))
                .ToList();
        }
    }
}

public sealed class ChatBadgeCatalog
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly IBadgeApi _api;
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, string> _urls = new Dictionary<string, string>();
    private Task? _loading;
    private DateTime _nextTryUtc;
    private int _generation;

    public ChatBadgeCatalog(IBadgeApi api) => _api = api;

    public bool IsLoaded => _urls.Count > 0;

    public bool IsLoading
    {
        get
        {
            lock (_gate) return _loading is { IsCompleted: false };
        }
    }

    public event Action? Loaded;

    public string? UrlFor(string setId, string id)
    {
        var urls = _urls;
        if (urls.TryGetValue(setId + "/" + id, out var url)) return url;
        if (!int.TryParse(id, out var wanted)) return null;

        var prefix = setId + "/";
        string? best = null;
        var bestVersion = -1;
        foreach (var (key, value) in urls)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(key[prefix.Length..], out var version)) continue;
            if (version <= wanted && version / 1000 == wanted / 1000 && version > bestVersion)
            {
                best = value;
                bestVersion = version;
            }
        }
        return best;
    }

    public Task EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (IsLoaded) return Task.CompletedTask;
            if (_loading is { IsCompleted: false }) return _loading;
            if (DateTime.UtcNow < _nextTryUtc) return Task.CompletedTask;
            _loading = LoadAsync();
            return _loading;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _urls = new Dictionary<string, string>();
            _loading = null;
            _nextTryUtc = default;
        }
    }

    private async Task LoadAsync()
    {
        int generation;
        lock (_gate) generation = _generation;
        try
        {
            var urls = await _api.GetBadgeImagesAsync(CancellationToken.None);
            lock (_gate)
            {
                if (generation != _generation) return;
                _urls = urls;
            }
            if (urls.Count > 0) Loaded?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Chat badges failed: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation) _nextTryUtc = DateTime.UtcNow + RetryAfter;
            }
        }
    }
}
