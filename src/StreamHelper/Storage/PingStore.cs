using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class PingStore : EventStore<ChatPing>
{
    public PingStore(string path) : base(path, nameof(ChatPing.Seen))
    {
        Load();
    }

    protected override string KeyOf(ChatPing item) => item.Key;

    protected override IEnumerable<ChatPing> OrderAscending(IEnumerable<ChatPing> items) =>
        items.OrderBy(p => p.AtUtc).ThenBy(p => p.Key, StringComparer.Ordinal);
}