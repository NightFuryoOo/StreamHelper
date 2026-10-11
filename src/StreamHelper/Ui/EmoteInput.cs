using System;
using System.Collections.Generic;
using System.Text;

namespace StreamHelper.Ui;

public readonly record struct EmoteSpan(int Start, int Length);

public static class EmoteInput
{
    public static IReadOnlyList<EmoteSpan> Images(string text, IReadOnlySet<int> shown, int caret, Func<string, bool> isEmote)
    {
        var spans = new List<EmoteSpan>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            var typing = caret > start && caret <= i;
            if ((!typing || shown.Contains(start)) && isEmote(text[start..i])) spans.Add(new EmoteSpan(start, i - start));
        }
        return spans;
    }

    public static string Flatten(string text)
    {
        var flat = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
            flat.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
        }
        return flat.ToString();
    }

    public static (string Text, int Caret, HashSet<int> Shown) Splice(string text, IEnumerable<EmoteSpan> shown, int start, int end, string insert, int maxLength)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        insert = Flatten(insert);
        var room = Math.Max(0, maxLength - (text.Length - (end - start)));
        if (insert.Length > room) insert = insert[..room];
        var delta = insert.Length - (end - start);
        var kept = new HashSet<int>();
        foreach (var span in shown)
        {
            if (span.Start + span.Length <= start) kept.Add(span.Start);
            else if (span.Start >= end) kept.Add(span.Start + delta);
        }
        return (text[..start] + insert + text[end..], start + insert.Length, kept);
    }
}
