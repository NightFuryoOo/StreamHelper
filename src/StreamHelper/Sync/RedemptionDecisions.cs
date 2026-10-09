using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Sync;

public sealed record DecisionResult(bool Success, string Message, bool Resolved = false);

public sealed record BulkDecisionResult(int Done, int Settled, int Failed, string? Problem)
{
    public string Summary(RedemptionDecision decision)
    {
        var parts = new List<string>();
        if (Done > 0) parts.Add(decision == RedemptionDecision.Fulfilled ? $"Принято: {Done}" : $"Отклонено, баллы возвращены: {Done}");
        if (Settled > 0) parts.Add($"уже обработано в Twitch: {Settled}");
        if (Failed > 0) parts.Add(Problem is { Length: > 0 } ? $"не получилось: {Failed} ({Problem})" : $"не получилось: {Failed}");
        if (parts.Count == 0) return "Нечего обрабатывать.";
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

public static class RedemptionDecisions
{
    public static async Task<BulkDecisionResult> DecideManyAsync(
        IRewardApi api, IReadOnlyList<Redemption> items, RedemptionDecision decision, Action<Redemption> onResolved, CancellationToken ct)
    {
        int done = 0, settled = 0, failed = 0;
        string? problem = null;
        foreach (var redemption in items)
        {
            if (redemption.IsBusy) continue;
            redemption.IsBusy = true;
            redemption.ConfirmingReject = false;
            try
            {
                var result = await DecideAsync(api, redemption, decision, ct);
                if (result.Success) done++;
                else if (result.Resolved) settled++;
                else
                {
                    failed++;
                    problem ??= result.Message;
                }
                if (result.Resolved)
                {
                    redemption.Selected = false;
                    onResolved(redemption);
                }
            }
            catch (Exception ex)
            {
                failed++;
                problem ??= ex.Message;
                break;
            }
            finally
            {
                redemption.IsBusy = false;
            }
        }
        return new BulkDecisionResult(done, settled, failed, problem);
    }

    public static async Task<DecisionResult> DecideAsync(
        IRewardApi api, Redemption redemption, RedemptionDecision decision, CancellationToken ct)
    {
        if (!redemption.CanManage || redemption.Status != RedemptionStatus.Pending)
        {
            return new DecisionResult(false, "Эту награду нельзя обработать из программы.");
        }

        var result = await api.UpdateRedemptionAsync(redemption.RewardId, redemption.Key, decision, ct);
        switch (result.Outcome)
        {
            case RedemptionUpdateOutcome.Done:
                redemption.Seen = true;
                if (decision == RedemptionDecision.Fulfilled)
                {
                    redemption.Done = true;
                    redemption.Status = RedemptionStatus.Accepted;
                    return new DecisionResult(true, $"Принято: {redemption.Name} · {redemption.Title}", Resolved: true);
                }
                redemption.Status = RedemptionStatus.Rejected;
                return new DecisionResult(true, $"Отклонено, баллы возвращены: {redemption.Name} · {redemption.Title}", Resolved: true);

            case RedemptionUpdateOutcome.AlreadyProcessed:
            case RedemptionUpdateOutcome.NotFound:
                redemption.Status = RedemptionStatus.Processed;
                return new DecisionResult(false, "Twitch говорит, что заказ уже обработан или отменён зрителем.", Resolved: true);

            case RedemptionUpdateOutcome.NotAllowed:
                redemption.CanManage = false;
                return new DecisionResult(false, "Twitch не разрешает управлять этой наградой: её создали не через программу.");

            default:
                return new DecisionResult(false, $"Не получилось: {result.Message}");
        }
    }
}
