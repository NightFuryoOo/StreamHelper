using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;

namespace StreamHelper.Sync;

public sealed record SavedIcon(string RewardTitle, string Folder);

public sealed record RewardIconsResult(
    IReadOnlyList<SavedIcon> Saved, int WithoutCustomImage, int Scaled, IReadOnlyList<string> Failures,
    int ReducedFiles = 0, int LargestBytes = 0);

public static class RewardIcons
{
    public static readonly int[] Sizes = { 28, 56, 112 };

    public const int MaxFileBytes = 25_000;

    public static async Task<RewardIconsResult> SaveAsync(
        IRewardApi api, bool canListManaged, string folder, CancellationToken ct)
    {
        var all = await api.GetRewardsAsync(ct);
        var managed = canListManaged ? await api.GetManageableRewardIdsAsync(ct) : new HashSet<string>();

        Directory.CreateDirectory(folder);
        var saved = new List<SavedIcon>();
        var failures = new List<string>();
        var withoutImage = 0;
        var scaled = 0;
        var reduced = 0;
        var largest = 0;
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reward in all.Where(r => !managed.Contains(r.Id)))
        {
            var urls = new[] { reward.ImageUrl1x, reward.ImageUrl2x, reward.ImageUrl4x };
            var anyUrl = urls.Any(u => u != null) || reward.ImageUrl != null;
            if (!anyUrl)
            {
                withoutImage++;
                continue;
            }

            var title = DisplayTitle(reward);
            try
            {
                var downloaded = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                async Task<byte[]?> Get(string? url)
                {
                    if (url == null) return null;
                    if (!downloaded.TryGetValue(url, out var data))
                    {
                        data = (await api.DownloadImageAsync(url, ct))?.Data;
                        downloaded[url] = data;
                    }
                    return data;
                }

                var best = await Get(reward.ImageUrl4x ?? reward.ImageUrl2x ?? reward.ImageUrl1x ?? reward.ImageUrl);
                if (best == null)
                {
                    failures.Add($"{title}: картинку не удалось скачать.");
                    continue;
                }

                var files = new List<(int Size, EncodedIcon Icon)>();
                var madeBySizing = false;
                for (var i = 0; i < Sizes.Length; i++)
                {
                    var own = await Get(urls[i]);
                    if (own == null) madeBySizing = true;
                    files.Add((Sizes[i], IconEncoder.Encode(own ?? best, Sizes[i], MaxFileBytes)));
                }

                var target = Path.Combine(folder, UniqueFolder(SafeFileName(reward), usedFolders));
                Directory.CreateDirectory(target);
                foreach (var (size, icon) in files)
                {
                    await File.WriteAllBytesAsync(Path.Combine(target, $"{size}x{size}.png"), icon.Png, ct);
                    largest = Math.Max(largest, icon.Png.Length);
                    if (icon.ColoursReduced) reduced++;
                }
                if (madeBySizing) scaled++;
                saved.Add(new SavedIcon(title, target));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{title}: {ex.Message}");
            }
        }

        return new RewardIconsResult(saved, withoutImage, scaled, failures, reduced, largest);
    }

    public static string SafeFileName(RewardInfo reward)
    {
        var cleaned = reward.Title.Replace("​", "");
        foreach (var bad in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(bad, '_');
        cleaned = cleaned.Trim().TrimEnd('.');
        if (cleaned.Length > 80) cleaned = cleaned[..80].TrimEnd();
        return cleaned.Length == 0 ? "reward-" + reward.Id : cleaned;
    }

    private static string UniqueFolder(string baseName, ISet<string> used)
    {
        var name = baseName;
        for (var n = 2; !used.Add(name); n++) name = $"{baseName} ({n})";
        return name;
    }

    private static string DisplayTitle(RewardInfo reward) => string.IsNullOrWhiteSpace(reward.Title) ? reward.Id : reward.Title;
}
