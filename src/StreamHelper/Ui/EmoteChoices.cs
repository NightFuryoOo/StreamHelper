using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Api;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public enum EmoteSection
{
    Recent,
    Channel,
    Mine,
    Twitch,
    SevenTv,
    Bttv,
    Ffz,
}

public sealed record EmoteChoice(string Name, string Url, EmoteSection Section, string Owner = "");

public sealed record EmoteShelf(EmoteSection Section, string Title, IReadOnlyList<EmoteChoice> Emotes, string Key = "", string? IconUrl = null);

public sealed record EmoteOwner(string Name, string IconUrl);

public static class EmoteChoices
{
    public const int RecentLimit = 24;

    public static IReadOnlyList<EmoteChoice> Collect(
        IReadOnlyList<TwitchEmote>? mine, IReadOnlyList<TwitchEmote> channel, IReadOnlyList<TwitchEmote> global,
        IReadOnlyList<ExtraEmote> extraChannel, IReadOnlyList<ExtraEmote> extraGlobal, string channelId, Func<TwitchEmote, string> url)
    {
        var result = new List<EmoteChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, string link, EmoteSection section, string owner = "")
        {
            if (name.Length > 0 && seen.Add(name)) result.Add(new EmoteChoice(name, link, section, owner));
        }

        if (mine != null)
        {
            foreach (var emote in mine.Where(e => e.OwnerId.Length > 0 && e.OwnerId == channelId)) Add(emote.Name, url(emote), EmoteSection.Channel, channelId);
            foreach (var emote in extraChannel) Add(emote.Name, emote.Url, EmoteSection.Channel, channelId);
            foreach (var emote in mine.Where(e => IsOwned(e) && e.OwnerId != channelId)) Add(emote.Name, url(emote), EmoteSection.Mine, emote.OwnerId);
            foreach (var emote in mine.Where(e => !IsOwned(e))) Add(emote.Name, url(emote), EmoteSection.Twitch);
            foreach (var emote in global) Add(emote.Name, url(emote), EmoteSection.Twitch);
        }
        else
        {
            foreach (var emote in channel) Add(emote.Name, url(emote), EmoteSection.Channel, channelId);
            foreach (var emote in extraChannel) Add(emote.Name, emote.Url, EmoteSection.Channel, channelId);
            foreach (var emote in global) Add(emote.Name, url(emote), EmoteSection.Twitch);
        }
        foreach (var emote in extraGlobal) Add(emote.Name, emote.Url, SectionOf(emote.Source));
        return result;
    }

    private static EmoteSection SectionOf(ExtraSource source) => source switch
    {
        ExtraSource.Bttv => EmoteSection.Bttv,
        ExtraSource.Ffz => EmoteSection.Ffz,
        _ => EmoteSection.SevenTv,
    };

    private static string KeyOf(EmoteSection section) => section switch
    {
        EmoteSection.Twitch => "twitch",
        EmoteSection.Bttv => "bttv",
        EmoteSection.Ffz => "ffz",
        _ => "7tv",
    };

    private static bool IsOwned(TwitchEmote emote) => emote.OwnerId.Length > 0 && emote.OwnerId != "0";

    public static IReadOnlyList<string> Owners(IReadOnlyList<EmoteChoice> all) =>
        all.Where(e => e.Owner.Length > 0).Select(e => e.Owner).Distinct(StringComparer.Ordinal).ToList();

    public static string Title(EmoteSection section) => section switch
    {
        EmoteSection.Recent => "Недавние",
        EmoteSection.Channel => "Канал",
        EmoteSection.Mine => "Другой канал",
        EmoteSection.Twitch => "Twitch",
        EmoteSection.Bttv => "BTTV",
        EmoteSection.Ffz => "FFZ",
        _ => "7TV",
    };

    public static IReadOnlyList<EmoteShelf> Shelves(
        IReadOnlyList<EmoteChoice> all, IReadOnlyList<string>? recent, string filter, IReadOnlyDictionary<string, EmoteOwner>? owners = null)
    {
        var shelves = new List<EmoteShelf>();
        var query = (filter ?? "").Trim();
        if (query.Length > 0)
        {
            var found = Match(all, query, int.MaxValue);
            if (found.Count > 0) shelves.Add(new EmoteShelf(EmoteSection.Recent, "Найдено", found, "found"));
            return shelves;
        }
        EmoteOwner? OwnerOf(string id) => owners != null && owners.TryGetValue(id, out var owner) ? owner : null;
        var byName = all.GroupBy(e => e.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var recentChoices = (recent ?? Array.Empty<string>()).Where(byName.ContainsKey).Select(n => byName[n] with { Section = EmoteSection.Recent }).ToList();
        if (recentChoices.Count > 0) shelves.Add(new EmoteShelf(EmoteSection.Recent, Title(EmoteSection.Recent), recentChoices, "recent"));

        var channel = all.Where(e => e.Section == EmoteSection.Channel).ToList();
        if (channel.Count > 0)
        {
            var owner = OwnerOf(channel[0].Owner);
            shelves.Add(new EmoteShelf(EmoteSection.Channel, owner is { Name.Length: > 0 } ? "Канал · " + owner.Name : Title(EmoteSection.Channel), channel, "channel", owner?.IconUrl));
        }
        foreach (var group in all.Where(e => e.Section == EmoteSection.Mine).GroupBy(e => e.Owner, StringComparer.Ordinal))
        {
            var owner = OwnerOf(group.Key);
            shelves.Add(new EmoteShelf(EmoteSection.Mine, owner is { Name.Length: > 0 } ? owner.Name : Title(EmoteSection.Mine), group.ToList(), "owner:" + group.Key, owner?.IconUrl));
        }
        foreach (var section in new[] { EmoteSection.Twitch, EmoteSection.SevenTv, EmoteSection.Bttv, EmoteSection.Ffz })
        {
            var emotes = all.Where(e => e.Section == section).ToList();
            if (emotes.Count > 0) shelves.Add(new EmoteShelf(section, Title(section), emotes, KeyOf(section)));
        }
        return shelves;
    }

    public static IReadOnlyList<EmoteChoice> Match(IReadOnlyList<EmoteChoice> all, string prefix, int max = 6)
    {
        if (prefix.Length == 0) return Array.Empty<EmoteChoice>();
        var starts = all.Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var inside = all.Where(e => !e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && e.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase));
        return starts.Concat(inside).Take(max).ToList();
    }

    public static List<string> Remember(IEnumerable<string>? recent, string name, int limit = RecentLimit)
    {
        var list = (recent ?? Array.Empty<string>()).Where(n => !string.Equals(n, name, StringComparison.Ordinal)).ToList();
        list.Insert(0, name);
        if (list.Count > limit) list.RemoveRange(limit, list.Count - limit);
        return list;
    }

    public static (string Text, int Caret) InsertAt(string text, int caret, string name)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var before = text[..caret];
        var after = text[caret..];
        var lead = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "";
        var tail = after.Length > 0 && char.IsWhiteSpace(after[0]) ? "" : " ";
        var inserted = lead + name + tail;
        return (before + inserted + after, before.Length + inserted.Length);
    }
}
