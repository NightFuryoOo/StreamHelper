using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Storage;

public sealed class DonationStore : EventStore<Donation>
{
    public DonationStore(string path)
        : base(path, nameof(Donation.Seen), nameof(Donation.Done), nameof(Donation.CustomName))
    {
        Load();
    }

    protected override string KeyOf(Donation item) => item.Key;

    protected override IEnumerable<Donation> OrderAscending(IEnumerable<Donation> items) => items.OrderBy(d => d.Id);
}
