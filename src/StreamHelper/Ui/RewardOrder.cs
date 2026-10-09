using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public static class RewardOrder
{
    public static List<RewardInfo> ByCost(IEnumerable<RewardInfo> rewards) =>
        rewards.OrderBy(r => r.Cost)
            .ThenBy(r => r.Title.Replace("​", ""), StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
