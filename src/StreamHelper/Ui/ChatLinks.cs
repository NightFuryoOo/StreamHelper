using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace StreamHelper.Ui;

public readonly record struct TextPart(string Text, Uri? Link);

public static class ChatLinks
{
    private static readonly Regex Candidate = new(@"\b(?:https?://|www\.)[^\s<>""]+", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string TrailingPunctuation = ".,!?:;'\"»…";

    public static IReadOnlyList<TextPart> Split(string text)
    {
        var parts = new List<TextPart>();
        var at = 0;
        foreach (Match match in Candidate.Matches(text))
        {
            if (match.Index < at) continue;
            var raw = Trim(match.Value);
            if (ToUri(raw) is not { } uri) continue;
            if (match.Index > at) parts.Add(new TextPart(text[at..match.Index], null));
            parts.Add(new TextPart(raw, uri));
            at = match.Index + raw.Length;
        }
        if (at < text.Length) parts.Add(new TextPart(text[at..], null));
        return parts;
    }

    public static Uri? ToUri(string url)
    {
        var full = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.Host.Contains('.') && !uri.Host.EndsWith('.') ? uri : null;
    }

    private static string Trim(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (TrailingPunctuation.Contains(last)) url = url[..^1];
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')')) url = url[..^1];
            else break;
        }
        return url;
    }
}