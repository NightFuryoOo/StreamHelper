using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class RedemptionStore : EventStore<Redemption>
{
    public RedemptionStore(string path)
        : base(path, nameof(Redemption.Seen), nameof(Redemption.Done), nameof(Redemption.Status), nameof(Redemption.CanManage))
    {
        Load();
    }

    protected override string KeyOf(Redemption item) => item.Key;

    protected override IEnumerable<Redemption> OrderAscending(IEnumerable<Redemption> items) =>
        items.OrderBy(r => r.AtUtc).ThenBy(r => r.Key, StringComparer.Ordinal);
}
