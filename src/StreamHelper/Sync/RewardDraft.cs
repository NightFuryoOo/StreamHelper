using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public enum CooldownUnit
{
    Seconds,
    Minutes,
    Hours,
}

public sealed record RewardDraft
{
    public string Title { get; init; } = "";
    public string Cost { get; init; } = "";
    public string Prompt { get; init; } = "";
    public bool UserInput { get; init; }
    public string Color { get; init; } = "";
    public bool LimitPerStream { get; init; }
    public string PerStream { get; init; } = "";
    public bool LimitPerUser { get; init; }
    public string PerUser { get; init; } = "";
    public bool Cooldown { get; init; }
    public string CooldownValue { get; init; } = "";
    public CooldownUnit CooldownUnit { get; init; } = CooldownUnit.Minutes;
    public bool SkipQueue { get; init; }
    public bool Enabled { get; init; } = true;
}

public static class RewardRules
{
    public const int MaxPrompt = 200;
    public const long MaxCost = 1_000_000_000;
    public const int MaxCooldownSeconds = 7 * 24 * 3600;

    public static readonly string[] Colors =
    {
        "#9146FF", "#1E69FF", "#00C8AF", "#34C759", "#FFCA1C", "#FF6905", "#E91916", "#FA1ED2", "#6B6B6B",
    };

    public static (RewardInfo? Reward, string Error) Build(RewardDraft draft, string id = "")
    {
        var title = RewardRename.Clean(draft.Title);
        if (RewardRename.Problem(title) is { } problem) return (null, problem);
        if (!long.TryParse(Digits(draft.Cost), NumberStyles.None, CultureInfo.InvariantCulture, out var cost) || cost < 1 || cost > MaxCost)
        {
            return (null, "Цена — целое число баллов от 1 до 1 000 000 000.");
        }
        var prompt = draft.Prompt.Trim();
        if (prompt.Length > MaxPrompt) return (null, $"Описание не длиннее {MaxPrompt} знаков.");
        if (NormalizeColor(draft.Color) is not { } color) return (null, "Цвет пиши как #9146FF или оставь пустым.");

        int? perStream = null;
        if (draft.LimitPerStream)
        {
            if (Count(draft.PerStream) is not { } value) return (null, "«Не больше за стрим» — целое число от 1.");
            perStream = value;
        }
        int? perUser = null;
        if (draft.LimitPerUser)
        {
            if (Count(draft.PerUser) is not { } value) return (null, "«Не больше на зрителя» — целое число от 1.");
            perUser = value;
        }
        int? cooldown = null;
        if (draft.Cooldown)
        {
            var seconds = Count(draft.CooldownValue) is { } value ? (long)value * UnitSeconds(draft.CooldownUnit) : 0;
            if (seconds < 1 || seconds > MaxCooldownSeconds) return (null, "Пауза между заказами — от 1 секунды до 7 суток.");
            cooldown = (int)seconds;
        }

        return (new RewardInfo(id, title, cost, draft.Enabled)
        {
            Prompt = prompt,
            BackgroundColor = color,
            IsUserInputRequired = draft.UserInput,
            MaxPerStream = perStream,
            MaxPerUserPerStream = perUser,
            GlobalCooldownSeconds = cooldown,
            SkipRequestQueue = draft.SkipQueue,
        }, "");
    }

    public static bool TitleTaken(string title, IEnumerable<string> otherTitles)
    {
        var name = RewardRename.Clean(title);
        return otherTitles.Any(other => string.Equals(RewardRename.Clean(other), name, StringComparison.OrdinalIgnoreCase));
    }

    public static RewardDraft FromReward(RewardInfo reward)
    {
        var cooldown = reward.GlobalCooldownSeconds ?? 0;
        var unit = cooldown > 0 && cooldown % 3600 == 0 ? CooldownUnit.Hours : cooldown > 0 && cooldown % 60 == 0 ? CooldownUnit.Minutes : cooldown > 0 ? CooldownUnit.Seconds : CooldownUnit.Minutes;
        return new RewardDraft
        {
            Title = RewardRename.Clean(reward.Title),
            Cost = reward.Cost.ToString(CultureInfo.InvariantCulture),
            Prompt = reward.Prompt,
            UserInput = reward.IsUserInputRequired,
            Color = NormalizeColor(reward.BackgroundColor) ?? "",
            LimitPerStream = reward.MaxPerStream is >= 1,
            PerStream = reward.MaxPerStream is >= 1 and var perStream ? perStream.ToString(CultureInfo.InvariantCulture) : "",
            LimitPerUser = reward.MaxPerUserPerStream is >= 1,
            PerUser = reward.MaxPerUserPerStream is >= 1 and var perUser ? perUser.ToString(CultureInfo.InvariantCulture) : "",
            Cooldown = cooldown > 0,
            CooldownValue = cooldown > 0 ? (cooldown / UnitSeconds(unit)).ToString(CultureInfo.InvariantCulture) : "",
            CooldownUnit = unit,
            SkipQueue = reward.SkipRequestQueue,
            Enabled = reward.IsEnabled,
        };
    }

    public static string? NormalizeColor(string text) => string.IsNullOrWhiteSpace(text) ? "" : HexColor.Normalize(text);

    public static int UnitSeconds(CooldownUnit unit) => unit switch
    {
        CooldownUnit.Hours => 3600,
        CooldownUnit.Minutes => 60,
        _ => 1,
    };

    private static string Digits(string text) => (text ?? "").Trim().Replace(" ", "").Replace("\u00A0", "");

    private static int? Count(string text) =>
        int.TryParse(Digits(text), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 1 ? value : null;
}