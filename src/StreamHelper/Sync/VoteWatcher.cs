using System;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class VoteWatcher : PollingService
{
    public static readonly TimeSpan HotInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan QuietInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ShowEndedFor = TimeSpan.FromSeconds(15);

    private readonly SettingsStore _settings;
    private readonly IVoteApi _api;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private ChannelVote? _poll;
    private ChannelVote? _prediction;

    public VoteWatcher(SettingsStore settings, IVoteApi api, Func<DateTime>? now = null)
    {
        _settings = settings;
        _api = api;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public event Action? Changed;

    public ChannelVote? Poll
    {
        get
        {
            lock (_gate) return _poll;
        }
    }

    public ChannelVote? Prediction
    {
        get
        {
            lock (_gate) return _prediction;
        }
    }

    public bool IsBusy(VoteKind kind) => (kind == VoteKind.Poll ? Poll : Prediction) is { Stage: not VoteStage.Ended };

    protected override string Name => "Twitch polls and predictions";

    protected override TimeSpan Interval => IsHot(Poll) || IsHot(Prediction) ? HotInterval : QuietInterval;

    internal override async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        if (!settings.HasTwitchTokens || (!settings.HasPollScope && !settings.HasPredictionScope))
        {
            Set(null, null);
            SetStatus(SyncState.NotConnected, "Нет прав на опросы и предикты.");
            return false;
        }
        if (!settings.IsOwnChannel)
        {
            Set(null, null);
            SetStatus(SyncState.NotConnected, "Опросы и предикты только на своём канале.");
            return false;
        }

        var poll = settings.HasPollScope ? await _api.GetLatestPollAsync(ct) : null;
        var prediction = settings.HasPredictionScope ? await _api.GetLatestPredictionAsync(ct) : null;
        Set(poll, prediction);
        SetStatus(SyncState.Ok, "Подключено");
        return true;
    }

    public void Show(ChannelVote vote)
    {
        if (vote.Kind == VoteKind.Poll) Set(vote, Prediction);
        else Set(Poll, vote);
        PollSoon();
    }

    private bool IsHot(ChannelVote? vote) =>
        vote != null && (vote.Stage != VoteStage.Ended || (vote.EndedUtc is { } ended && _now() - ended < ShowEndedFor + HotInterval));

    private void Set(ChannelVote? poll, ChannelVote? prediction)
    {
        lock (_gate)
        {
            if (poll?.Signature == _poll?.Signature && prediction?.Signature == _prediction?.Signature) return;
            _poll = poll;
            _prediction = prediction;
        }
        Changed?.Invoke();
    }
}
