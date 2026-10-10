using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record UpdateAsset(string Name, string Url, long Size, string? Digest = null);

public sealed record UpdateRelease(
    Version Version, string Tag, string Name, string Notes, string PageUrl, UpdateAsset? Exe, UpdateAsset? Checksum, DateTime? PublishedUtc = null)
{
    public bool CanInstall => Exe != null && (Exe.Digest != null || Checksum != null);
}

public static class AppUpdate
{
    public const string Owner = "NightFuryoOo";
    public const string Repository = "StreamHelper";
    public const string DefaultApi = "https://api.github.com";
    public const string ExeAsset = "StreamHelper.exe";
    public const string ChecksumAsset = "StreamHelper.exe.sha256";
    public const long MaxExeBytes = 300L * 1024 * 1024;
    public const int MaxNotesLength = 1500;

    public static readonly Version OldestChoosable = new(1, 0, 0, 1);

    public static string LatestUrl(string? apiBase) =>
        $"{(string.IsNullOrWhiteSpace(apiBase) ? DefaultApi : apiBase.Trim()).TrimEnd('/')}/repos/{Owner}/{Repository}/releases/latest";

    public static Version? ParseVersion(string? tag)
    {
        var text = (tag ?? "").Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..].TrimStart('.', '-', '_', ' ');
        return Version.TryParse(text, out var version) ? AppVersion.Normalize(version) : null;
    }

    public static string ReleasesUrl(string? apiBase) =>
        $"{(string.IsNullOrWhiteSpace(apiBase) ? DefaultApi : apiBase.Trim()).TrimEnd('/')}/repos/{Owner}/{Repository}/releases?per_page=100";

    public static bool IsNewer(UpdateRelease release, Version current) => release.Version > AppVersion.Normalize(current);

    public static bool ShouldOffer(UpdateRelease release, Version current, string? skipped) =>
        IsNewer(release, current) && (ParseVersion(skipped) is not { } skip || release.Version > skip);

    public static string? SkipAfterChoosing(Version chosen, IEnumerable<UpdateRelease> known)
    {
        var newest = known.Select(r => r.Version).DefaultIfEmpty(chosen).Max()!;
        return newest > AppVersion.Normalize(chosen) ? newest.ToString(4) : null;
    }

    public static IReadOnlyList<UpdateRelease> Choosable(IEnumerable<UpdateRelease> releases) =>
        releases
            .Where(r => r.Version >= OldestChoosable && r.CanInstall)
            .GroupBy(r => r.Version)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version)
            .ToList();

    public static IReadOnlyList<UpdateRelease> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var releases = new List<UpdateRelease>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return releases;
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (Read(element) is { } release) releases.Add(release);
        }
        return releases;
    }

    public static UpdateRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    private static UpdateRelease? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
        var tag = Text(root, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                var url = Text(asset, "browser_download_url");
                if (url.Length == 0) continue;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                assets.Add(new UpdateAsset(Text(asset, "name"), url, size, ParseDigest(Text(asset, "digest"))));
            }
        }

        DateTime? published = DateTime.TryParse(Text(root, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        return new UpdateRelease(
            version, tag, Text(root, "name"), CleanNotes(Text(root, "body")), Text(root, "html_url"),
            Find(assets, ExeAsset), Find(assets, ChecksumAsset), published);
    }

    public static string? ParseDigest(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim();
        return Regex.IsMatch(hex, "^[0-9A-Fa-f]{64}$") ? hex.ToUpperInvariant() : null;
    }

    public static string? ParseChecksum(string text)
    {
        var match = Regex.Match(text ?? "", @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])");
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    public static string CleanNotes(string body)
    {
        var lines = (body ?? "").Replace("\r", "").Split('\n')
            .Select(line => Regex.Replace(line.Trim(), @"^#{1,6}\s*", "").Replace("**", "").Replace("__", ""))
            .ToList();
        var kept = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (kept.Count == 0 || kept[^1].Length == 0)) continue;
            kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        var text = string.Join("\n", kept);
        return text.Length <= MaxNotesLength ? text : text[..MaxNotesLength].TrimEnd() + "…";
    }

    private static UpdateAsset? Find(IEnumerable<UpdateAsset> assets, string name) =>
        assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
