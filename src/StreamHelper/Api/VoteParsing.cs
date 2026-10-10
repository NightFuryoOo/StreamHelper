using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using StreamHelper.Models;

namespace StreamHelper.Api;

public static class VoteParsing
{
    public static IReadOnlyList<ChannelVote> ParsePolls(string json) => Parse(json, VoteKind.Poll);

    public static IReadOnlyList<ChannelVote> ParsePredictions(string json) => Parse(json, VoteKind.Prediction);

    private static IReadOnlyList<ChannelVote> Parse(string json, VoteKind kind)
    {
        var votes = new List<ChannelVote>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return votes;
        foreach (var item in data.EnumerateArray())
        {
            var vote = kind == VoteKind.Poll ? ReadPoll(item) : ReadPrediction(item);
            if (vote != null) votes.Add(vote);
        }
        return votes;
    }

    private static ChannelVote? ReadPoll(JsonElement item)
    {
        var status = Text(item, "status").ToUpperInvariant();
        VoteStage stage;
        switch (status)
        {
            case "ACTIVE":
                stage = VoteStage.Active;
                break;
            case "COMPLETED":
            case "TERMINATED":
                stage = VoteStage.Ended;
                break;
            default:
                return null;
        }
        var options = new List<VoteOption>();
        if (item.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                options.Add(new VoteOption(Text(choice, "id"), Text(choice, "title"), Number(choice, "votes"), Number(choice, "channel_points_votes"), 0, ""));
            }
        }
        var started = Time(item, "started_at") ?? DateTime.UtcNow;
        var duration = Number(item, "duration");
        return new ChannelVote(
            VoteKind.Poll, Text(item, "id"), Text(item, "title"), stage, status, options,
            started, duration > 0 ? started.AddSeconds(duration) : null, Time(item, "ended_at"), null);
    }

    private static ChannelVote? ReadPrediction(JsonElement item)
    {
        var status = Text(item, "status").ToUpperInvariant();
        var stage = status switch
        {
            "ACTIVE" => VoteStage.Active,
            "LOCKED" => VoteStage.Locked,
            "RESOLVED" or "CANCELED" => VoteStage.Ended,
            _ => (VoteStage?)null,
        };
        if (stage == null) return null;
        var options = new List<VoteOption>();
        if (item.TryGetProperty("outcomes", out var outcomes) && outcomes.ValueKind == JsonValueKind.Array)
        {
            foreach (var outcome in outcomes.EnumerateArray())
            {
                var users = Number(outcome, "users");
                options.Add(new VoteOption(Text(outcome, "id"), Text(outcome, "title"), users, Number(outcome, "channel_points"), users, Text(outcome, "color").ToUpperInvariant()));
            }
        }
        var started = Time(item, "created_at") ?? DateTime.UtcNow;
        var window = Number(item, "prediction_window");
        var winner = Text(item, "winning_outcome_id");
        return new ChannelVote(
            VoteKind.Prediction, Text(item, "id"), Text(item, "title"), stage.Value, status, options,
            started, window > 0 ? started.AddSeconds(window) : null, Time(item, "ended_at"), winner.Length > 0 ? winner : null);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    private static DateTime? Time(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text.Length > 0 && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;
    }
}
