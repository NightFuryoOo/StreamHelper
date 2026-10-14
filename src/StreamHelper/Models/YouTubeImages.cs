using System;

namespace StreamHelper.Models;

public static class YouTubeImages
{
    public const string DefaultBase = "https://i.ytimg.com";

    public static string Base { get; set; } = DefaultBase;

    public static string Thumbnail(string videoId, string? imagesBase = null) =>
        $"{(imagesBase ?? Base).TrimEnd('/')}/vi/{Uri.EscapeDataString(videoId)}/mqdefault.jpg";
}
