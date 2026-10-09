using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class FollowerStore : EventStore<Follower>
{
    public FollowerStore(string path) : base(path, nameof(Follower.Seen))
    {
        Load();
    }

    protected override string KeyOf(Follower item) => item.Key;

    protected override IEnumerable<Follower> OrderAscending(IEnumerable<Follower> items) =>
        items.OrderBy(f => f.FollowedAtUtc).ThenBy(f => f.UserId, System.StringComparer.Ordinal);
}
