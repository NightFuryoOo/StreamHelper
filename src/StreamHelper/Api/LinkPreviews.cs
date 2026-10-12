using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public enum PreviewKind
{
    TwitchClip,
    YouTube,
}

public sealed record PreviewTarget(PreviewKind Kind, string Id, string Url);

public sealed record LinkPreview(
    PreviewKind Kind, string Url, string Title, string Channel, string Credit, int? Views, TimeSpan? Duration, string ThumbnailUrl);

public interface IClipApi
{
    Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct);
}

public interface IVideoApi
{
    Task<LinkPreview?> GetVideoAsync(string videoId, CancellationToken ct);
}

public static class PreviewLinks
{
    private static readonly Regex ClipSlug = new(@"^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly Regex VideoId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);

    public static PreviewTarget? Find(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        var host = uri.Host.ToLowerInvariant();
        foreach (var prefix in new[] { "www.", "m.", "music." })
        {
            if (host.StartsWith(prefix, StringComparison.Ordinal)) host = host[prefix.Length..];
        }
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = host switch
        {
            "clips.twitch.tv" when parts.Length == 1 && parts[0] == "embed" => Query(uri, "clip"),
            "clips.twitch.tv" when parts.Length == 1 => parts[0],
            "twitch.tv" when parts.Length == 3 && parts[1] == "clip" => parts[2],
            "youtube.com" when parts.Length == 1 && parts[0] == "watch" => Query(uri, "v"),
            "youtube.com" when parts.Length == 2 && parts[0] is "shorts" or "live" or "embed" => parts[1],
            "youtu.be" when parts.Length == 1 => parts[0],
            _ => null,
        };
        if (id == null) return null;
        var clip = host.EndsWith("twitch.tv", StringComparison.Ordinal);
        if (!(clip ? ClipSlug : VideoId).IsMatch(id)) return null;
        return new PreviewTarget(clip ? PreviewKind.TwitchClip : PreviewKind.YouTube, id, uri.AbsoluteUri);
    }

    private static string? Query(Uri uri, string name)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            if (cut > 0 && pair[..cut] == name) return Uri.UnescapeDataString(pair[(cut + 1)..]);
        }
        return null;
    }

    public static LinkPreview? ParseClip(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var clip in data.EnumerateArray())
        {
            var title = Text(clip, "title");
            var url = Text(clip, "url");
            if (title.Length == 0 && url.Length == 0) continue;
            int? views = clip.TryGetProperty("view_count", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            TimeSpan? length = clip.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var s) && s > 0
                ? TimeSpan.FromSeconds(Math.Round(s))
                : null;
            return new LinkPreview(PreviewKind.TwitchClip, url, title, Text(clip, "broadcaster_name"), Text(clip, "creator_name"), views, length, Text(clip, "thumbnail_url"));
        }
        return null;
    }

    public static LinkPreview? ParseVideo(string json, string videoId, string thumbnailBase)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var title = Text(root, "title");
        if (title.Length == 0) return null;
        return new LinkPreview(PreviewKind.YouTube, "https://www.youtube.com/watch?v=" + videoId, title, Text(root, "author_name"), "", null, null,
            $"{thumbnailBase.TrimEnd('/')}/vi/{Uri.EscapeDataString(videoId)}/mqdefault.jpg");
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

public sealed class YouTubeClient : IVideoApi
{
    public const string DefaultSite = "https://www.youtube.com";
    public const string DefaultImages = "https://i.ytimg.com";

    private readonly HttpClient _http;
    private readonly string _site;
    private readonly string _images;

    public YouTubeClient(HttpClient http, string? site = null, string? images = null)
    {
        _http = http;
        _site = string.IsNullOrWhiteSpace(site) ? DefaultSite : site.TrimEnd('/');
        _images = string.IsNullOrWhiteSpace(images) ? DefaultImages : images.TrimEnd('/');
    }

    public async Task<LinkPreview?> GetVideoAsync(string videoId, CancellationToken ct)
    {
        var watch = "https://www.youtube.com/watch?v=" + videoId;
        var url = $"{_site}/oembed?url={Uri.EscapeDataString(watch)}&format=json";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(string.Format(CultureInfo.InvariantCulture, "YouTube: HTTP {0}.", (int)response.StatusCode));
        return PreviewLinks.ParseVideo(await response.Content.ReadAsStringAsync(ct), videoId, _images);
    }
}
