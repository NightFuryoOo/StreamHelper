using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;

namespace StreamHelper.Sync;

public sealed record RewardRenameRun(bool Success, string Title, string Message);

public static class RewardRename
{
    public static string Clean(string title) => title.Replace(RewardSync.InvisibleMark, "").Trim();

    public static string? Problem(string name) =>
        name.Length == 0 ? "Название не может быть пустым."
        : name.Length > RewardSync.TitleLimit ? $"Название не длиннее {RewardSync.TitleLimit} знаков."
        : null;

    public static IReadOnlyList<string> Candidates(string name, IEnumerable<string> otherTitles)
    {
        var taken = otherTitles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new List<string> { name };
        if (RewardSync.InvisibleTitle(name) is { } invisible) options.Add(invisible);
        return options.Where(option => !taken.Contains(option)).ToList();
    }

    public static async Task<RewardRenameRun> RunAsync(
        IRewardApi api, string rewardId, string currentTitle, string newName, IEnumerable<string> otherTitles, CancellationToken ct)
    {
        var name = Clean(newName);
        if (Problem(name) is { } problem) return new RewardRenameRun(false, currentTitle, problem);
        if (string.Equals(name, Clean(currentTitle), StringComparison.Ordinal)) return new RewardRenameRun(true, currentTitle, "");

        var takenMessage = $"Название «{name}» уже занято другой наградой на канале.";
        foreach (var title in Candidates(name, otherTitles))
        {
            var result = await api.SetRewardTitleAsync(rewardId, title, ct);
            if (result.Outcome == RewardRenameOutcome.Renamed) return new RewardRenameRun(true, title, "");
            if (result.Outcome == RewardRenameOutcome.Failed) return new RewardRenameRun(false, currentTitle, result.Message);
        }
        return new RewardRenameRun(false, currentTitle, takenMessage);
    }
}
