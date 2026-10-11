using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Ui;

public sealed record Chatter(string Login, string DisplayName, string Color);

public readonly record struct MentionToken(int Start, int End, string Prefix);

public sealed class RecentChatters
{
    public const int Limit = 300;

    private readonly List<Chatter> _list = new();

    public int Count => _list.Count;

    public void Saw(string login, string displayName, string color)
    {
        if (string.IsNullOrWhiteSpace(login)) return;
        _list.RemoveAll(c => string.Equals(c.Login, login, StringComparison.OrdinalIgnoreCase));
        _list.Insert(0, new Chatter(login, string.IsNullOrWhiteSpace(displayName) ? login : displayName, color));
        if (_list.Count > Limit) _list.RemoveRange(Limit, _list.Count - Limit);
    }

    public void Clear() => _list.Clear();

    public IReadOnlyList<Chatter> Match(string prefix, string? exceptLogin, int max = 6, Chatter? pinned = null, IReadOnlyList<Chatter>? everyone = null)
    {
        var others = _list.Where(c => !string.Equals(c.Login, exceptLogin, StringComparison.OrdinalIgnoreCase)).ToList();
        if (everyone is { Count: > 0 })
        {
            var known = new HashSet<string>(others.Select(c => c.Login), StringComparer.OrdinalIgnoreCase);
            if (exceptLogin != null) known.Add(exceptLogin);
            others.AddRange(everyone.Where(c => c.Login.Length > 0 && known.Add(c.Login)).OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase));
        }
        if (pinned != null && pinned.Login.Length > 0 && !string.Equals(pinned.Login, exceptLogin, StringComparison.OrdinalIgnoreCase))
        {
            var own = others.FirstOrDefault(c => string.Equals(c.Login, pinned.Login, StringComparison.OrdinalIgnoreCase));
            others.RemoveAll(c => string.Equals(c.Login, pinned.Login, StringComparison.OrdinalIgnoreCase));
            others.Insert(0, own ?? pinned);
        }
        if (prefix.Length == 0) return others.Take(max).ToList();
        bool Starts(Chatter c) =>
            c.Login.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || c.DisplayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool Has(Chatter c) =>
            c.Login.Contains(prefix, StringComparison.OrdinalIgnoreCase) || c.DisplayName.Contains(prefix, StringComparison.OrdinalIgnoreCase);
        return others.Where(Starts).Concat(others.Where(c => !Starts(c) && Has(c))).Take(max).ToList();
    }
}

public static class MentionText
{
    public static MentionToken? At(string text, int caret) => WordAt(text, caret, '@', 0);

    public static MentionToken? EmoteAt(string text, int caret) => WordAt(text, caret, ':', 2);

    private static MentionToken? WordAt(string text, int caret, char mark, int minPrefix)
    {
        if (caret < 0 || caret > text.Length) return null;
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        if (start >= text.Length || text[start] != mark || caret == start) return null;
        var prefix = text[(start + 1)..caret];
        if (prefix.Contains(mark) || prefix.Length < minPrefix) return null;
        var end = caret;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        return new MentionToken(start, end, prefix);
    }

    public static string NameFor(Chatter chatter) =>
        string.Equals(chatter.DisplayName, chatter.Login, StringComparison.OrdinalIgnoreCase) ? chatter.DisplayName : chatter.Login;

    public static (string Text, int Caret) Insert(string text, MentionToken token, string name) => Replace(text, token, "@" + name);

    public static (string Text, int Caret) Replace(string text, MentionToken token, string replacement)
    {
        var before = text[..token.Start];
        var after = text[token.End..];
        var inserted = replacement;
        if (after.Length == 0 || !char.IsWhiteSpace(after[0])) return (before + inserted + " " + after, before.Length + inserted.Length + 1);
        return (before + inserted + after, before.Length + inserted.Length + 1);
    }
}
