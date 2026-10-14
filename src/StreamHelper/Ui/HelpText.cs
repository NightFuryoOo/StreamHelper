using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StreamHelper.Ui;

public enum HelpSpanKind
{
    Text,
    Link,
    Code,
}

public sealed record HelpSpan(string Text, HelpSpanKind Kind = HelpSpanKind.Text, string Url = "");

public static class HelpText
{
    public const char NoBreakHyphen = '\u2011';

    private static readonly Regex Markup = new(@"\[(?<text>[^\]]+)\]\((?<url>https://[^)\s]+)\)|`(?<code>[^`]+)`", RegexOptions.Compiled);

    public static IReadOnlyList<HelpSpan> Parse(string paragraph)
    {
        var spans = new List<HelpSpan>();
        var at = 0;
        foreach (Match match in Markup.Matches(paragraph))
        {
            if (match.Index > at) spans.Add(new HelpSpan(paragraph[at..match.Index]));
            spans.Add(match.Groups["code"].Success
                ? new HelpSpan(match.Groups["code"].Value, HelpSpanKind.Code)
                : new HelpSpan(match.Groups["text"].Value, HelpSpanKind.Link, match.Groups["url"].Value));
            at = match.Index + match.Length;
        }
        if (at < paragraph.Length) spans.Add(new HelpSpan(paragraph[at..]));
        return spans;
    }

    public static string Plain(string text) =>
        Markup.Replace(text, match => match.Groups["code"].Success ? match.Groups["code"].Value : match.Groups["text"].Value);

    public static string ForDisplay(string code) => code.Replace('-', NoBreakHyphen);

    public static string ForCopy(string text) => text.Replace(NoBreakHyphen, '-');
}
