using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record UpdateAsset(string Name, string Url, long Size);

public sealed record UpdateRelease(Version Version, string Tag, string Name, string Notes, string PageUrl, UpdateAsset? Exe, UpdateAsset? Checksum);

public static class AppUpdate
{
    public const string Owner = "NightFuryoOo";
    public const string Repository = "StreamHelper";
    public const string DefaultApi = "https://api.github.com";
    public const string ExeAsset = "StreamHelper.exe";
    public const string ChecksumAsset = "StreamHelper.exe.sha256";
    public const long MaxExeBytes = 300L * 1024 * 1024;
    public const int MaxNotesLength = 1500;

    public static string LatestUrl(string? apiBase) =>
        $"{(string.IsNullOrWhiteSpace(apiBase) ? DefaultApi : apiBase.Trim()).TrimEnd('/')}/repos/{Owner}/{Repository}/releases/latest";

    public static Version? ParseVersion(string? tag)
    {
        var text = (tag ?? "").Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        return Version.TryParse(text, out var version) ? AppVersion.Normalize(version) : null;
    }

    public static bool IsNewer(UpdateRelease release, Version current) => release.Version > AppVersion.Normalize(current);

    public static UpdateRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
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
                assets.Add(new UpdateAsset(Text(asset, "name"), url, size));
            }
        }

        return new UpdateRelease(
            version, tag, Text(root, "name"), CleanNotes(Text(root, "body")), Text(root, "html_url"),
            Find(assets, ExeAsset), Find(assets, ChecksumAsset));
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
