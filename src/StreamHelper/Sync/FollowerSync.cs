using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public sealed record FollowerSyncResult(
    IReadOnlyList<Follower> Items, DateTime LastFollowerAtUtc, IReadOnlyList<string> LastUserIds, bool Baselined);

public static class FollowerSync
{
    public const int MaxPages = 10;

    public static async Task<FollowerSyncResult> FetchNewAsync(
        IFollowerSource source, DateTime lastAt, IReadOnlyCollection<string> lastIds, bool baselined, CancellationToken ct)
    {
        var first = await source.GetFollowersAsync(null, ct);

        if (!baselined)
        {
            if (first.Items.Count == 0) return new FollowerSyncResult(Array.Empty<Follower>(), default, Array.Empty<string>(), true);
            var newest = first.Items.Max(f => f.FollowedAtUtc);
            var ids = first.Items.Where(f => f.FollowedAtUtc == newest).Select(f => f.UserId).Distinct().ToList();
            return new FollowerSyncResult(Array.Empty<Follower>(), newest, ids, true);
        }

        var known = lastIds.ToHashSet();
        var fresh = new List<Follower>();
        var page = first;
        var number = 1;
        while (true)
        {
            fresh.AddRange(page.Items.Where(f =>
                f.FollowedAtUtc > lastAt || (f.FollowedAtUtc == lastAt && !known.Contains(f.UserId))));
            var reachedKnown = page.Items.Count == 0 || page.Items.Min(f => f.FollowedAtUtc) < lastAt;
            if (reachedKnown || page.Cursor == null || number >= MaxPages) break;
            number++;
            page = await source.GetFollowersAsync(page.Cursor, ct);
        }

        var ordered = fresh
            .GroupBy(f => f.Key)
            .Select(g => g.First())
            .OrderBy(f => f.FollowedAtUtc)
            .ThenBy(f => f.UserId, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0) return new FollowerSyncResult(ordered, lastAt, lastIds.ToList(), true);

        var newestAt = ordered.Max(f => f.FollowedAtUtc);
        var newestIds = ordered.Where(f => f.FollowedAtUtc == newestAt).Select(f => f.UserId);
        var carried = newestAt == lastAt ? lastIds : Array.Empty<string>();
        return new FollowerSyncResult(ordered, newestAt, carried.Concat(newestIds).Distinct().ToList(), true);
    }
}
