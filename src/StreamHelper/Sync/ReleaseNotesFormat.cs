using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StreamHelper.Sync;

public enum NotesBlockKind
{
    Paragraph,
    Heading,
    Bullet,
}

public sealed record NotesSpan(string Text, bool Bold);

public sealed record NotesBlock(NotesBlockKind Kind, IReadOnlyList<NotesSpan> Spans, int Level = 0, string Marker = "", bool GapBefore = false);

public static class ReleaseNotesFormat
{
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*)$");
    private static readonly Regex Bullet = new(@"^[-*+]\s+(.*)$");
    private static readonly Regex Numbered = new(@"^(\d{1,3}[.)])\s+(.*)$");
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)\s]+)\)");

    public static IReadOnlyList<NotesBlock> Parse(string body)
    {
        var blocks = new List<NotesBlock>();
        var gap = false;
        foreach (var raw in (body ?? "").Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                gap = blocks.Count > 0;
                continue;
            }
            if (Regex.IsMatch(line, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                gap = blocks.Count > 0;
                continue;
            }
            NotesBlock block;
            if (Heading.Match(line) is { Success: true } heading)
            {
                block = new NotesBlock(NotesBlockKind.Heading, Spans(heading.Groups[2].Value), heading.Groups[1].Length, GapBefore: gap);
            }
            else if (Bullet.Match(line) is { Success: true } bullet)
            {
                block = new NotesBlock(NotesBlockKind.Bullet, Spans(bullet.Groups[1].Value), Marker: "•", GapBefore: gap);
            }
            else if (Numbered.Match(line) is { Success: true } numbered)
            {
                block = new NotesBlock(NotesBlockKind.Bullet, Spans(numbered.Groups[2].Value), Marker: numbered.Groups[1].Value, GapBefore: gap);
            }
            else
            {
                block = new NotesBlock(NotesBlockKind.Paragraph, Spans(line), GapBefore: gap);
            }
            blocks.Add(block);
            gap = false;
        }
        return blocks;
    }

    public static IReadOnlyList<NotesSpan> Spans(string text)
    {
        var plain = Link.Replace(text, "$1").Replace("`", "").Replace("__", "**");
        var spans = new List<NotesSpan>();
        var bold = false;
        var parts = plain.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) spans.Add(new NotesSpan(parts[i], bold && i < parts.Length - 1));
            bold = !bold;
        }
        if (parts.Length % 2 == 0 && spans.Count > 0)
        {
            var last = spans[^1];
            spans[^1] = last with { Text = "**" + last.Text, Bold = false };
        }
        return spans;
    }
}
