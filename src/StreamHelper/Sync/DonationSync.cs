using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public sealed record SyncResult(IReadOnlyList<Donation> Items, long LastDonationId, bool Baselined, string Order);

public static class DonationSync
{
    public const int MaxPages = 10;

    public static async Task<SyncResult> FetchNewAsync(
        IDonationSource source, long lastId, bool baselined, CancellationToken ct)
    {
        var first = await source.GetDonationsAsync(1, ct);
        var ascending = first.Items.Count >= 2 && first.Items[0].Id < first.Items[^1].Id;
        var order = ascending ? "ascending" : "descending";

        if (!baselined)
        {
            var newestPage = ascending && first.LastPage > 1 ? await source.GetDonationsAsync(first.LastPage, ct) : first;
            var newest = newestPage.Items.Count == 0 ? 0 : newestPage.Items.Max(d => d.Id);
            return new SyncResult(Array.Empty<Donation>(), Math.Max(lastId, newest), true, order);
        }

        var fresh = new List<Donation>();
        if (!ascending)
        {
            var page = first;
            var number = 1;
            while (true)
            {
                var newer = page.Items.Where(d => d.Id > lastId).ToList();
                fresh.AddRange(newer);
                if (newer.Count < page.Items.Count || !page.HasNext || number >= MaxPages) break;
                number++;
                page = await source.GetDonationsAsync(number, ct);
            }
        }
        else
        {
            var number = first.LastPage;
            var fetched = 0;
            while (number >= 1 && fetched < MaxPages)
            {
                var page = number == 1 ? first : await source.GetDonationsAsync(number, ct);
                fetched++;
                var newer = page.Items.Where(d => d.Id > lastId).ToList();
                fresh.AddRange(newer);
                if (newer.Count < page.Items.Count) break;
                number--;
            }
        }

        var ordered = fresh.GroupBy(d => d.Id).Select(g => g.First()).OrderBy(d => d.Id).ToList();
        var newLast = ordered.Count == 0 ? lastId : ordered[^1].Id;
        return new SyncResult(ordered, newLast, true, order);
    }
}
