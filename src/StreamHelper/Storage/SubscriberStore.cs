using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class SubscriberStore : EventStore<Subscriber>
{
    public SubscriberStore(string path) : base(path, nameof(Subscriber.Seen))
    {
        Load();
    }

    protected override string KeyOf(Subscriber item) => item.Key;

    protected override IEnumerable<Subscriber> OrderAscending(IEnumerable<Subscriber> items) =>
        items.OrderBy(s => s.AtUtc).ThenBy(s => s.Key, StringComparer.Ordinal);
}
