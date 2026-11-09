using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class PinWatcher
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(2);

    private readonly IModerationApi _api;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _timer = new() { Interval = Every };
    private readonly DispatcherTimer _soon = new() { Interval = AfterChange };
    private int _version;
    private bool _busy;
    private bool _again;
    private string _lastError = "";

    public PinWatcher(IModerationApi api, Func<AppSettings> settings)
    {
        _api = api;
        _settings = settings;
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _soon.Tick += (_, _) =>
        {
            _soon.Stop();
            _ = RefreshAsync();
        };
    }

    public PinnedMessage? Pinned { get; private set; }

    public event Action? Changed;

    public bool IsWatching => _timer.IsEnabled;

    public bool Allowed
    {
        get
        {
            var settings = _settings();
            return settings.HasTwitchTokens && settings.HasDeleteMessagesScope && !settings.IsViewerChannel;
        }
    }

    public void Watch(bool shown)
    {
        if (!shown)
        {
            _timer.Stop();
            _soon.Stop();
            return;
        }
        if (_timer.IsEnabled) return;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Reset()
    {
        _version++;
        _lastError = "";
        Set(null);
        if (_timer.IsEnabled) _ = RefreshAsync();
    }

    public void Show(PinnedMessage? pinned)
    {
        _version++;
        Set(pinned);
        if (!_timer.IsEnabled) return;
        _soon.Stop();
        _soon.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _soon.Stop();
    }

    public async Task RefreshAsync()
    {
        if (!Allowed)
        {
            Set(null);
            return;
        }
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        var version = _version;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var check = await _api.GetPinnedAsync(cts.Token);
            if (version != _version) return;
            if (check.Success)
            {
                _lastError = "";
                Set(check.Pinned);
            }
            else Note(check.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            if (version == _version) Note(ex.Message);
        }
        finally
        {
            _busy = false;
            if (_again)
            {
                _again = false;
                _ = RefreshAsync();
            }
        }
    }

    private void Note(string error)
    {
        if (error == _lastError) return;
        _lastError = error;
        Log.Write("Reading the pinned chat message failed: " + error);
    }

    private void Set(PinnedMessage? pinned)
    {
        if (PinCardText.SamePin(Pinned, pinned)) return;
        Pinned = pinned;
        Changed?.Invoke();
    }
}
