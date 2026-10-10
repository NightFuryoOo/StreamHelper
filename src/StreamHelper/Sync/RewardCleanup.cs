using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record CleanupResult(IReadOnlyList<string> DeletedIds, IReadOnlyList<string> Failures);

public static class RewardCleanup
{
    public static async Task<CleanupResult> DeleteAsync(IRewardApi api, IEnumerable<RewardInfo> rewards, CancellationToken ct)
    {
        var deleted = new List<string>();
        var failures = new List<string>();
        foreach (var reward in rewards)
        {
            var title = string.IsNullOrWhiteSpace(reward.Title) ? reward.Id : reward.Title.Replace("​", "");
            var result = await api.DeleteRewardAsync(reward.Id, ct);
            switch (result.Outcome)
            {
                case RewardDeleteOutcome.Deleted:
                case RewardDeleteOutcome.NotFound:
                    deleted.Add(reward.Id);
                    break;
                case RewardDeleteOutcome.NotAllowed:
                    failures.Add($"{title}: Twitch не разрешает удалять эту награду, её создали не через программу.");
                    break;
                default:
                    failures.Add($"{title}: {result.Message}");
                    break;
            }
        }
        return new CleanupResult(deleted, failures);
    }

    public static void ApplyLocally(AppSettings settings, RedemptionStore redemptions, IReadOnlyCollection<string> deletedIds)
    {
        if (deletedIds.Count == 0) return;
        var gone = deletedIds.ToHashSet(StringComparer.Ordinal);
        settings.ManagedRewardIds = settings.ManagedRewardIds.Where(id => !gone.Contains(id)).ToList();
        settings.HiddenRewardIds = settings.HiddenRewardIds.Where(id => !gone.Contains(id)).ToList();
        settings.MuteSwitchedOffRewardIds = settings.MuteSwitchedOffRewardIds.Where(id => !gone.Contains(id)).ToList();
        foreach (var key in settings.RewardCopies.Where(pair => gone.Contains(pair.Value)).Select(pair => pair.Key).ToList())
        {
            settings.RewardCopies.Remove(key);
        }

        foreach (var redemption in redemptions.Items.Where(r => gone.Contains(r.RewardId)).ToList())
        {
            redemption.CanManage = false;
            if (redemption.Status == RedemptionStatus.Pending) redemption.Status = RedemptionStatus.Processed;
        }
    }
}
