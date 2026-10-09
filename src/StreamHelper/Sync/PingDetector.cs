using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StreamHelper.Api;

namespace StreamHelper.Sync;

public static class PingDetector
{
    public const int MaxWords = 50;
    public const int MaxWordLength = 60;

    private static readonly ConcurrentDictionary<string, Regex?> Patterns = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsPing(
        ChatMessage message, string streamerId, string streamerLogin, IEnumerable<string> ignoredChatters,
        IEnumerable<string>? words = null)
    {
        if (streamerId.Length == 0 && streamerLogin.Length == 0) return false;
        if (streamerId.Length > 0 && message.ChatterId == streamerId) return false;
        if (streamerLogin.Length > 0 && message.ChatterLogin.Equals(streamerLogin, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsIgnored(message.ChatterLogin, ignoredChatters)) return false;

        foreach (var mention in message.Mentions)
        {
            if (streamerId.Length > 0 && mention.UserId == streamerId) return true;
            if (streamerLogin.Length > 0 && mention.Login.Equals(streamerLogin, StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (streamerLogin.Length > 0 &&
            Regex.IsMatch(message.Text, "(?<![A-Za-z0-9_])@" + Regex.Escape(streamerLogin) + "(?![A-Za-z0-9_])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }
        return words != null && ContainsWord(message.Text, words);
    }

    public static bool ContainsWord(string text, IEnumerable<string> words)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var word in words)
        {
            var pattern = PatternOf(word);
            if (pattern != null && pattern.IsMatch(text)) return true;
        }
        return false;
    }

    private static Regex? PatternOf(string entry) =>
        Patterns.GetOrAdd(entry, static e => Build(e));

    private static Regex? Build(string entry)
    {
        entry = entry.Trim();
        if (entry.Length == 0 || entry.All(c => c == '*' || char.IsWhiteSpace(c))) return null;
        const string wordChar = "[\\p{L}\\p{N}_]";
        var body = new System.Text.StringBuilder();
        for (var i = 0; i < entry.Length; i++)
        {
            var c = entry[i];
            if (c == '*')
            {
                body.Append(wordChar).Append('*');
                while (i + 1 < entry.Length && entry[i + 1] == '*') i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                body.Append("\\s+");
                while (i + 1 < entry.Length && char.IsWhiteSpace(entry[i + 1])) i++;
            }
            else
            {
                body.Append(Regex.Escape(c.ToString()));
            }
        }
        return new Regex("(?<!" + wordChar + ")" + body + "(?!" + wordChar + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    public static List<string> ParseWords(string text)
    {
        var result = new List<string>();
        foreach (var part in text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = string.Join(" ", part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (entry.Length > MaxWordLength) entry = entry[..MaxWordLength].TrimEnd();
            if (entry.Length == 0 || entry.All(c => c == '*' || char.IsWhiteSpace(c))) continue;
            if (result.Any(x => string.Equals(x, entry, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(entry);
            if (result.Count >= MaxWords) break;
        }
        return result;
    }

    public static bool IsIgnored(string chatterLogin, IEnumerable<string> ignoredChatters) =>
        chatterLogin.Length > 0 &&
        ignoredChatters.Any(name => string.Equals(name.Trim().TrimStart('@'), chatterLogin, StringComparison.OrdinalIgnoreCase));

    public static List<string> ParseIgnoredList(string text) =>
        text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim().TrimStart('@').ToLowerInvariant())
            .Where(name => name.Length > 0)
            .Distinct()
            .ToList();
}