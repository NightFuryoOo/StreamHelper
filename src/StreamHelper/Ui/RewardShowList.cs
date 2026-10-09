using System;
using System.Collections.Generic;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public static class RewardShowList
{
    public static List<RewardRow> Build(
        IReadOnlyList<RewardInfo> all, ICollection<string> managedIds, AppSettings settings, bool canManage, Func<string, int>? pendingOf = null)
    {
        var rows = new List<RewardRow>();
        foreach (var reward in RewardOrder.ByCost(all))
        {
            var own = managedIds.Contains(reward.Id);
            if (settings.OnlyOwnRewards && !own) continue;
            rows.Add(new RewardRow(
                reward.Id, reward.Title, reward.Cost, reward.IsEnabled, own, own && canManage,
                own ? pendingOf?.Invoke(reward.Id) ?? 0 : 0, !settings.IsRewardHidden(reward.Id)));
        }
        return rows;
    }

    public static void Apply(AppSettings settings, RewardRow row, bool shown)
    {
        if (shown) settings.HiddenRewardIds.RemoveAll(id => id == row.Id);
        else if (!settings.IsRewardHidden(row.Id)) settings.HiddenRewardIds.Add(row.Id);
    }
}
