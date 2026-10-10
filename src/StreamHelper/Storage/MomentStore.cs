using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class MomentStore : EventStore<ChannelMoment>
{
    public MomentStore(string path) : base(path, nameof(ChannelMoment.Seen))
    {
        Load();
    }

    protected override string KeyOf(ChannelMoment item) => item.Key;

    protected override IEnumerable<ChannelMoment> OrderAscending(IEnumerable<ChannelMoment> items) => items.OrderBy(m => m.AtUtc);
}
