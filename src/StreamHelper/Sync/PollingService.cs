using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public enum SyncState
{
    NotConnected,
    Ok,
    NeedsLogin,
    Error,
}

public sealed record SyncStatus(SyncState State, string Message);

public interface ISyncWorker
{
    SyncStatus Status { get; }

    event Action<SyncStatus>? StatusChanged;

    void Start();

    void Stop();
}

public abstract class PollingService : ISyncWorker
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    protected abstract string Name { get; }

    protected virtual TimeSpan Interval => TimeSpan.FromSeconds(8);

    public SyncStatus Status { get; private set; } = new(SyncState.NotConnected, "Не подключено");

    public event Action<SyncStatus>? StatusChanged;

    internal abstract Task<bool> PollOnceAsync(CancellationToken ct);

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
        _loop = null;
    }

    public void PollSoon() => _wake.TrySetResult();

    protected void SetStatus(SyncState state, string message)
    {
        var next = new SyncStatus(state, message);
        if (Status == next) return;
        Status = next;
        StatusChanged?.Invoke(next);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync(ct) ? Interval : IdleInterval;
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (AuthRequiredException ex)
            {
                SetStatus(SyncState.NeedsLogin, ex.Message);
                delay = IdleInterval;
            }
            catch (Exception ex)
            {
                failures++;
                Log.Write($"{Name} poll failed: {ex.Message}");
                SetStatus(SyncState.Error, ex is HttpRequestException ? ex.Message : "Ошибка: " + ex.Message);
                delay = TimeSpan.FromSeconds(Math.Min(Interval.TotalSeconds * Math.Pow(2, failures - 1), MaxBackoff.TotalSeconds));
            }

            var wake = _wake;
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Task.WhenAny(Task.Delay(delay, delayCts.Token), wake.Task);
            delayCts.Cancel();
            if (wake.Task.IsCompleted)
            {
                _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
