using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public static class DaAlertMatch
{
    public static bool Handles(DaAlertEvent alert) => alert.Type is DaMediaFeed.SubAlertType or DaMediaFeed.GiftAlertType;

    public static Subscriber? Find(IEnumerable<Subscriber> subscribers, DaAlertEvent alert)
    {
        var all = subscribers.ToList();
        if (all.FirstOrDefault(s => s.DaAlertId == alert.Id && s.DaAlertType == alert.Type) is { } known) return known;
        var free = all.Where(s => s.DaAlertId == null);
        var candidates = alert.Type switch
        {
            DaMediaFeed.SubAlertType => free.Where(s => s.Kind is SubscriptionKind.New or SubscriptionKind.Resub && Named(s, alert.Username, alert.Name) && SameMonths(s, alert)),
            DaMediaFeed.GiftAlertType => free.Where(s => s.Kind == SubscriptionKind.Gift && s.GiftTotal <= 1 && Gifter(s, alert.Name)),
            _ => Enumerable.Empty<Subscriber>(),
        };
        return candidates.OrderByDescending(s => s.AtUtc).FirstOrDefault();
    }

    private static bool SameMonths(Subscriber subscriber, DaAlertEvent alert)
    {
        if (alert.Context == "resub") return subscriber.Kind == SubscriptionKind.Resub && (subscriber.Months <= 0 || alert.Months <= 0 || subscriber.Months == alert.Months);
        if (alert.Context == "sub") return subscriber.Kind == SubscriptionKind.New;
        return true;
    }

    private static bool Gifter(Subscriber subscriber, string name)
    {
        if (subscriber.IsAnonymous) return name.Length == 0 || name.Contains("anonymous", StringComparison.OrdinalIgnoreCase);
        return Named(subscriber, name, "");
    }

    private static bool Named(Subscriber subscriber, string first, string second)
    {
        bool Same(string a, string b) => a.Length > 0 && b.Length > 0 && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        return Same(subscriber.Login, first) || Same(subscriber.DisplayName, first) || Same(subscriber.Login, second) || Same(subscriber.DisplayName, second);
    }
}
