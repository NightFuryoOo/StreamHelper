using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public static class RedemptionRouting
{
    public static (List<Redemption> Listed, List<Redemption> SoundOnly) Split(AppSettings settings, IEnumerable<Redemption> items)
    {
        var listed = new List<Redemption>();
        var soundOnly = new List<Redemption>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.RewardId) && settings.IsRewardHidden(item.RewardId)) soundOnly.Add(item);
            else listed.Add(item);
        }
        return (listed, soundOnly);
    }
}