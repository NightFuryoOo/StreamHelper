using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class ArchiveRecorder
{
    public static readonly TimeSpan Tail = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    public static bool IsUpdate(object item, string property) => item switch
    {
        Donation => property == nameof(Donation.HasVideo),
        Subscriber => property == nameof(Subscriber.Recipients),
        Redemption redemption => property == nameof(Redemption.HasVideo) || property == nameof(Redemption.VideoTitle) && redemption.HasVideo,
        _ => false,
    };

    private readonly ChatArchive _archive;
    private readonly ILiveStreamApi _live;
    private readonly SettingsStore _settings;
    private readonly ChatFeed _feed;
    private readonly DonationStore _donations;
    private readonly SubscriberStore _subscribers;
    private readonly RedemptionStore _redemptions;
    private readonly MomentStore _moments;
    private readonly Func<DateTime> _now;
    private readonly ArchiveClips? _clips;
    private readonly List<INotifyPropertyChanged> _watched = new();
    private DateTime? _offlineSinceUtc;
    private CancellationTokenSource? _cts;

    public ArchiveRecorder(
        ChatArchive archive, ILiveStreamApi live, SettingsStore settings, ChatFeed feed, DonationStore donations,
        SubscriberStore subscribers, RedemptionStore redemptions, MomentStore moments, Func<DateTime>? now = null, IStreamClipsApi? clips = null)
    {
        _archive = archive;
        _live = live;
        _settings = settings;
        _feed = feed;
        _donations = donations;
        _subscribers = subscribers;
        _redemptions = redemptions;
        _moments = moments;
        _now = now ?? (() => DateTime.UtcNow);
        if (clips != null) _clips = new ArchiveClips(archive, clips, () => _settings.Current.TwitchUserId, _now);
        _archive.CloseDangling();
        _feed.Message += OnMessage;
        _feed.Deleted += OnDeleted;
        _feed.Moderated += OnModerated;
        _donations.Items.CollectionChanged += OnAdded;
        _subscribers.Items.CollectionChanged += OnAdded;
        _redemptions.Items.CollectionChanged += OnAdded;
        _moments.Items.CollectionChanged += OnAdded;
    }

    public ChatArchive Archive => _archive;

    public bool IsRecording => _archive.Open != null;

    public bool AddClip(StreamClip clip)
    {
        if (!Enabled || _archive.Open is not { } open || clip.CreatedUtc < open.StartUtc) return false;
        return _archive.SaveClips(open.Id, new[] { clip }, null) > 0;
    }

    private bool Enabled
    {
        get
        {
            var settings = _settings.Current;
            return settings.ArchiveEnabled && settings.TwitchUserId.Length > 0;
        }
    }

    private bool ChatIsOwn => _settings.Current.IsOwnChannel;

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _archive.Flush();
    }

    public void CheckSoon()
    {
        if (_cts != null) Start();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(ct);
                if (_clips != null && Enabled) await _clips.CheckAsync(ct);
            }
            catch (Exception ex)
            {
                Log.Write("Stream archive check failed: " + ex.Message);
            }
            try
            {
                await Task.Delay(CheckEvery, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            Apply(null, stopNow: true);
            return;
        }
        var userId = _settings.Current.TwitchUserId;
        IReadOnlyList<LiveStream> live;
        try
        {
            live = await _live.GetLiveStreamsAsync(new[] { userId }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write("Stream archive could not ask Twitch about the stream: " + ex.Message);
            return;
        }
        if (ct.IsCancellationRequested) return;
        Apply(live.FirstOrDefault(s => s.UserId == userId));
    }

    public void Apply(LiveStream? stream, bool stopNow = false)
    {
        var now = _now();
        if (stream != null && !stopNow)
        {
            _offlineSinceUtc = null;
            if (_archive.Open == null)
            {
                var opened = _archive.Begin(stream.StartedUtc ?? now, stream.Title, stream.Game, MergeWithin);
                Backfill(opened.StartUtc);
                Log.Write("Stream archive: recording the stream from " + opened.Id + ".");
            }
            else _archive.Describe(stream.Title, stream.Game);
            return;
        }
        if (_archive.Open == null) return;
        _offlineSinceUtc ??= now;
        if (!stopNow && now - _offlineSinceUtc.Value < Tail) return;
        _archive.End(_offlineSinceUtc.Value);
        _offlineSinceUtc = null;
        Unwatch();
        Log.Write("Stream archive: the stream is over.");
    }

    private void Backfill(DateTime startUtc)
    {
        if (ChatIsOwn)
        {
            foreach (var message in _feed.Since(startUtc)) Record(message);
            foreach (var message in _feed.Since(startUtc))
            {
                if (_feed.IsDeleted(message.MessageId)) Mark(new[] { message.MessageId });
                if (_feed.ModerationOf(message.MessageId) is { } moderation) Mark(new[] { message.MessageId }, moderation);
            }
        }
        foreach (var item in _donations.Items.Reverse()) Record(item, startUtc);
        foreach (var item in _subscribers.Items.Reverse()) Record(item, startUtc);
        foreach (var item in _redemptions.Items.Reverse()) Record(item, startUtc);
        foreach (var item in _moments.Items.Reverse()) Record(item, startUtc);
    }

    private void OnMessage(ChatMessage message)
    {
        if (ChatIsOwn) Record(message);
    }

    private void Record(ChatMessage message) =>
        _archive.Write(new ArchiveLine { Kind = ArchiveKind.Chat, AtUtc = message.AtUtc, Chat = message });

    private void OnDeleted(IReadOnlyList<string> ids)
    {
        if (ChatIsOwn) Mark(ids);
    }

    private void OnModerated(IReadOnlyList<string> ids, ChatModeration moderation)
    {
        if (ChatIsOwn) Mark(ids, moderation);
    }

    private void Mark(IReadOnlyList<string> ids, ChatModeration? moderation = null)
    {
        if (_archive.Open == null || ids.Count == 0) return;
        _archive.Write(new ArchiveLine
        {
            Kind = moderation == null ? ArchiveKind.Deleted : ArchiveKind.Moderated,
            AtUtc = _now(),
            Ids = ids.ToList(),
            Moderation = moderation,
        });
    }

    private void OnAdded(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        if (_archive.Open is not { } open) return;
        foreach (var item in e.NewItems) Record(item, open.StartUtc);
    }

    private void Record(object item, DateTime startUtc)
    {
        if (LineOf(item) is not { } line || IsTest(item) || line.AtUtc != default && line.AtUtc < startUtc) return;
        if (!_archive.Write(line)) return;
        if (item is INotifyPropertyChanged changing)
        {
            changing.PropertyChanged += OnItemChanged;
            _watched.Add(changing);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender == null || e.PropertyName == null || !IsUpdate(sender, e.PropertyName)) return;
        if (LineOf(sender) is { } line && _archive.Has(line)) _archive.Write(line, update: true);
    }

    private void Unwatch()
    {
        foreach (var item in _watched) item.PropertyChanged -= OnItemChanged;
        _watched.Clear();
    }

    public static ArchiveLine? LineOf(object item) => item switch
    {
        Donation donation => new ArchiveLine { Kind = ArchiveKind.Donation, AtUtc = donation.CreatedAtUtc, Donation = donation },
        Subscriber subscriber => new ArchiveLine { Kind = ArchiveKind.Subscriber, AtUtc = subscriber.AtUtc, Subscriber = subscriber },
        Redemption redemption => new ArchiveLine { Kind = ArchiveKind.Redemption, AtUtc = redemption.AtUtc, Redemption = redemption },
        ChannelMoment moment => new ArchiveLine { Kind = ArchiveKind.Moment, AtUtc = moment.AtUtc, Moment = moment },
        _ => null,
    };

    public static bool IsTest(object item) => item switch
    {
        Donation donation => donation.IsDonationAlerts && donation.Id < 0,
        Subscriber subscriber => subscriber.Key.StartsWith("test-", StringComparison.Ordinal),
        Redemption redemption => redemption.Key.StartsWith("test-", StringComparison.Ordinal),
        ChannelMoment moment => moment.Key.StartsWith("test-", StringComparison.Ordinal),
        _ => false,
    };
}
