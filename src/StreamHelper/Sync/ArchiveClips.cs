using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class ArchiveClips
{
    public static readonly TimeSpan LiveEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly ChatArchive _archive;
    private readonly IStreamClipsApi _api;
    private readonly Func<string> _broadcasterId;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, DateTime> _failedUtc = new(StringComparer.Ordinal);
    private string _lastError = "";

    public ArchiveClips(ChatArchive archive, IStreamClipsApi api, Func<string> broadcasterId, Func<DateTime>? now = null)
    {
        _archive = archive;
        _api = api;
        _broadcasterId = broadcasterId;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public static DateTime WindowEnd(ArchiveStream stream, DateTime nowUtc) => stream.EndUtc is { } end ? end + ArchiveRecorder.Tail : nowUtc;

    public static bool IsSettled(ArchiveStream stream) =>
        stream.EndUtc is { } end && stream.ClipsCheckedUtc is { } done && done >= end + ArchiveRecorder.Tail + Settle;

    public ArchiveStream? Due(DateTime nowUtc)
    {
        var open = _archive.Open;
        if (open != null && !Failing(open, nowUtc) && (open.ClipsCheckedUtc is not { } last || nowUtc - last >= LiveEvery)) return open;
        return _archive.Streams().FirstOrDefault(stream =>
            !ReferenceEquals(stream, open) && stream.EndUtc is { } end && !IsSettled(stream) &&
            nowUtc >= end + ArchiveRecorder.Tail + Settle && !Failing(stream, nowUtc));
    }

    private bool Failing(ArchiveStream stream, DateTime nowUtc) => _failedUtc.TryGetValue(stream.Id, out var at) && nowUtc - at < RetryAfter;

    public async Task<bool> CheckAsync(CancellationToken ct)
    {
        var now = _now();
        var broadcaster = _broadcasterId();
        if (broadcaster.Length == 0 || Due(now) is not { } stream) return false;
        var from = stream.StartUtc;
        var to = WindowEnd(stream, now);
        IReadOnlyList<StreamClip> clips;
        try
        {
            clips = await _api.GetStreamClipsAsync(broadcaster, from, to, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _failedUtc[stream.Id] = now;
            if (ex.Message != _lastError) Log.Write("Stream archive could not ask Twitch for clips: " + ex.Message);
            _lastError = ex.Message;
            return false;
        }
        _failedUtc.Remove(stream.Id);
        _lastError = "";
        var inside = clips.Where(clip => clip.CreatedUtc >= from && clip.CreatedUtc <= to).ToList();
        var added = await Task.Run(() => _archive.SaveClips(stream.Id, inside, now));
        if (added > 0) Log.Write($"Stream archive: {added} new clip(s) in {stream.Id}.");
        return true;
    }
}
