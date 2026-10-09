using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;

namespace StreamHelper.Sync;

public sealed record RewardCopy(string OriginalId, string CopyId);

public sealed record RewardSyncResult(
    int CreatedInvisible,
    int CreatedVisible,
    int AlreadyCopied,
    int SkippedByLimit,
    IReadOnlyList<string> Failures,
    IReadOnlyList<RewardCopy> Copies,
    IReadOnlySet<string> ManagedIds)
{
    public int Created => CreatedInvisible + CreatedVisible;
}

public static class RewardSync
{
    public const int MaxRewardsPerChannel = 50;
    public const int TitleLimit = 45;
    public const string InvisibleMark = "​";
    public const string VisibleSuffix = " (копия)";

    public static string? InvisibleTitle(string title) =>
        title.Length + InvisibleMark.Length <= TitleLimit ? title + InvisibleMark : null;

    public static string VisibleTitle(string title)
    {
        var room = TitleLimit - VisibleSuffix.Length;
        return (title.Length > room ? title[..room].TrimEnd() : title) + VisibleSuffix;
    }

    public static async Task<RewardSyncResult> RunAsync(
        IRewardApi api, IReadOnlyDictionary<string, string> knownCopies, CancellationToken ct)
    {
        var all = await api.GetRewardsAsync(ct);
        var managed = await api.GetManageableRewardIdsAsync(ct);
        var managedIds = new HashSet<string>(managed, StringComparer.Ordinal);
        var existingIds = all.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var managedTitles = all.Where(r => managedIds.Contains(r.Id)).Select(r => r.Title).ToHashSet(StringComparer.Ordinal);

        var copies = new List<RewardCopy>();
        var failures = new List<string>();
        var invisible = 0;
        var visible = 0;
        var already = 0;
        var skipped = 0;
        var slots = MaxRewardsPerChannel - all.Count;

        foreach (var original in all.Where(r => !managedIds.Contains(r.Id)))
        {
            if (knownCopies.TryGetValue(original.Id, out var known) && existingIds.Contains(known))
            {
                already++;
                continue;
            }
            var invisibleTitle = InvisibleTitle(original.Title);
            if ((invisibleTitle != null && managedTitles.Contains(invisibleTitle)) || managedTitles.Contains(VisibleTitle(original.Title)))
            {
                already++;
                continue;
            }
            if (slots <= 0)
            {
                skipped++;
                continue;
            }

            var attemptedInvisible = false;
            RewardCreateResult? result = null;
            if (invisibleTitle != null)
            {
                attemptedInvisible = true;
                result = await api.CreateRewardAsync(original, invisibleTitle, ct);
            }
            if (result == null || result.Outcome == RewardCreateOutcome.TitleTaken)
            {
                attemptedInvisible = false;
                result = await api.CreateRewardAsync(original, VisibleTitle(original.Title), ct);
            }

            switch (result.Outcome)
            {
                case RewardCreateOutcome.Created when result.Id != null:
                    copies.Add(new RewardCopy(original.Id, result.Id));
                    managedIds.Add(result.Id);
                    slots--;
                    if (attemptedInvisible) invisible++;
                    else visible++;
                    break;
                case RewardCreateOutcome.Created:
                    failures.Add($"{original.Title}: Twitch создал награду, но не вернул её идентификатор.");
                    slots--;
                    break;
                case RewardCreateOutcome.LimitReached:
                    slots = 0;
                    skipped++;
                    break;
                case RewardCreateOutcome.TitleTaken:
                    failures.Add($"{original.Title}: такое название уже занято.");
                    break;
                default:
                    failures.Add($"{original.Title}: {result.Message}");
                    break;
            }
        }

        return new RewardSyncResult(invisible, visible, already, skipped, failures, copies, managedIds);
    }
}
