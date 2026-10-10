using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Models;

public enum VoteKind
{
    Poll,
    Prediction,
}

public enum VoteStage
{
    Active,
    Locked,
    Ended,
}

public sealed record VoteOption(string Id, string Title, long Votes, long Points, long Users, string Color);

public sealed record ChannelVote(
    VoteKind Kind, string Id, string Title, VoteStage Stage, string Status, IReadOnlyList<VoteOption> Options,
    DateTime StartedUtc, DateTime? EndsUtc, DateTime? EndedUtc, string? WinnerId)
{
    public bool IsCanceled => Kind == VoteKind.Prediction && Status == "CANCELED";

    public string Signature =>
        $"{Kind}|{Id}|{Title}|{Stage}|{Status}|{EndsUtc:O}|{EndedUtc:O}|{WinnerId}|" +
        string.Join(";", Options.Select(o => $"{o.Id}:{o.Title}:{o.Votes}:{o.Points}:{o.Users}"));
}

public sealed record PollDraft(string Title, IReadOnlyList<string> Choices, int Seconds, bool PointsVoting, int PointsPerVote);

public sealed record PredictionDraft(string Title, IReadOnlyList<string> Outcomes, int Seconds);

public static class VoteRules
{
    public const int PollTitleMax = 60;
    public const int PredictionTitleMax = 45;
    public const int OptionMax = 25;
    public const int OptionsMin = 2;
    public const int PollOptionsMax = 5;
    public const int PredictionOptionsMax = 10;
    public const int PollSecondsMin = 15;
    public const int PredictionSecondsMin = 30;
    public const int SecondsMax = 1800;
    public const int PointsPerVoteMax = 1_000_000;

    public static int TitleMax(VoteKind kind) => kind == VoteKind.Poll ? PollTitleMax : PredictionTitleMax;

    public static int OptionsMax(VoteKind kind) => kind == VoteKind.Poll ? PollOptionsMax : PredictionOptionsMax;

    public static int SecondsMin(VoteKind kind) => kind == VoteKind.Poll ? PollSecondsMin : PredictionSecondsMin;

    public static string? CheckCommon(VoteKind kind, string title, IReadOnlyList<string> options, int seconds)
    {
        if (title.Length == 0) return "Напиши вопрос.";
        if (title.Length > TitleMax(kind)) return $"Вопрос не длиннее {TitleMax(kind)} знаков.";
        var word = kind == VoteKind.Poll ? "варианта" : "исхода";
        if (options.Count < OptionsMin) return $"Нужно хотя бы {OptionsMin} {word}.";
        if (options.Count > OptionsMax(kind)) return $"Не больше {OptionsMax(kind)} {(kind == VoteKind.Poll ? "вариантов" : "исходов")}.";
        if (options.FirstOrDefault(o => o.Length > OptionMax) is { } longOne) return $"«{longOne}» длиннее {OptionMax} знаков.";
        if (options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
        {
            return kind == VoteKind.Poll ? "Варианты не должны повторяться." : "Исходы не должны повторяться.";
        }
        if (seconds < SecondsMin(kind) || seconds > SecondsMax)
        {
            return $"Время от {SecondsMin(kind)} секунд до {SecondsMax / 60} минут.";
        }
        return null;
    }

    public static (PollDraft? Draft, string? Problem) Poll(string title, IEnumerable<string> choices, int seconds, bool pointsVoting, int pointsPerVote)
    {
        var cleanTitle = title.Trim();
        var cleanChoices = Clean(choices);
        if (CheckCommon(VoteKind.Poll, cleanTitle, cleanChoices, seconds) is { } problem) return (null, problem);
        if (pointsVoting && (pointsPerVote < 1 || pointsPerVote > PointsPerVoteMax))
        {
            return (null, $"Цена голоса от 1 до {PointsPerVoteMax:N0} баллов.");
        }
        return (new PollDraft(cleanTitle, cleanChoices, seconds, pointsVoting, pointsVoting ? pointsPerVote : 0), null);
    }

    public static (PredictionDraft? Draft, string? Problem) Prediction(string title, IEnumerable<string> outcomes, int seconds)
    {
        var cleanTitle = title.Trim();
        var cleanOutcomes = Clean(outcomes);
        if (CheckCommon(VoteKind.Prediction, cleanTitle, cleanOutcomes, seconds) is { } problem) return (null, problem);
        return (new PredictionDraft(cleanTitle, cleanOutcomes, seconds), null);
    }

    private static List<string> Clean(IEnumerable<string> options) =>
        options.Select(o => (o ?? "").Trim()).Where(o => o.Length > 0).ToList();
}
