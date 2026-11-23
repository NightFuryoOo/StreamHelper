using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public sealed class GiftRecipientBook
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan Near = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, GiftNotice> _bundles = new(StringComparer.Ordinal);
    private readonly List<GiftNotice> _waiting = new();

    public int Waiting => _waiting.Count;

    public IReadOnlyList<Subscriber> Add(IEnumerable<Subscriber> subscribers, GiftNotice notice, DateTime nowUtc)
    {
        Forget(nowUtc);
        if (notice.Kind == GiftNoticeKind.Bundle)
        {
            if (notice.BundleId.Length > 0) _bundles[notice.BundleId] = notice;
            return Retry(subscribers, nowUtc);
        }
        if (Place(subscribers.ToList(), notice) is { } card) return new[] { card };
        _waiting.Add(notice);
        return Array.Empty<Subscriber>();
    }

    public IReadOnlyList<Subscriber> Retry(IEnumerable<Subscriber> subscribers, DateTime nowUtc)
    {
        Forget(nowUtc);
        if (_waiting.Count == 0) return Array.Empty<Subscriber>();
        var all = subscribers.ToList();
        var changed = new List<Subscriber>();
        foreach (var notice in _waiting.ToList())
        {
            if (Place(all, notice) is not { } card) continue;
            _waiting.Remove(notice);
            if (!changed.Contains(card)) changed.Add(card);
        }
        return changed;
    }

    private Subscriber? Place(List<Subscriber> all, GiftNotice notice)
    {
        var gifter = notice.BundleId.Length > 0 && _bundles.TryGetValue(notice.BundleId, out var bundle) ? bundle : notice;
        var card = notice.BundleId.Length > 0 ? all.FirstOrDefault(s => s.GiftBundleId == notice.BundleId) : null;
        card ??= all
            .Where(s => s.Kind == SubscriptionKind.Gift && s.HasRoomForRecipient && (s.GiftBundleId == null || s.GiftBundleId == notice.BundleId))
            .Where(s => SameGifter(s, gifter) && (s.AtUtc == default || (s.AtUtc - notice.AtUtc).Duration() <= Near))
            .Where(s => notice.BundleId.Length > 0 || s.GiftTotal <= 1)
            .OrderBy(s => (s.AtUtc - notice.AtUtc).Duration())
            .FirstOrDefault();
        if (card == null) return null;
        card.AddRecipient(notice.RecipientLabel, notice.BundleId);
        return card;
    }

    private static bool SameGifter(Subscriber card, GiftNotice gifter)
    {
        if (gifter.Anonymous || card.IsAnonymous) return gifter.Anonymous && card.IsAnonymous;
        bool Same(string a, string b) => a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return Same(card.Login, gifter.GifterLogin) || Same(card.DisplayName, gifter.GifterName) || Same(card.Login, gifter.GifterName);
    }

    private void Forget(DateTime nowUtc)
    {
        _waiting.RemoveAll(notice => nowUtc - notice.AtUtc > Keep);
        foreach (var stale in _bundles.Where(pair => nowUtc - pair.Value.AtUtc > Keep).Select(pair => pair.Key).ToList()) _bundles.Remove(stale);
    }
}
