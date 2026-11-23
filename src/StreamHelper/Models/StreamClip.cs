using System;

namespace StreamHelper.Models;

public sealed record StreamClip
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string CreatorId { get; init; } = "";
    public string CreatorName { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public double Seconds { get; init; }
    public string ThumbnailUrl { get; init; } = "";
}
