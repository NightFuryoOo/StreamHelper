using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;

namespace StreamHelper.Api;

public sealed record VoteResult(bool Success, string Message, ChannelVote? Vote = null);

public enum PredictionChange
{
    Lock,
    Resolve,
    Cancel,
}

public interface IVoteApi
{
    Task<ChannelVote?> GetLatestPollAsync(CancellationToken ct);

    Task<ChannelVote?> GetLatestPredictionAsync(CancellationToken ct);

    Task<ChannelVote?> GetLastEndedAsync(VoteKind kind, CancellationToken ct);

    Task<VoteResult> CreatePollAsync(PollDraft draft, CancellationToken ct);

    Task<VoteResult> EndPollAsync(string pollId, CancellationToken ct);

    Task<VoteResult> CreatePredictionAsync(PredictionDraft draft, CancellationToken ct);

    Task<VoteResult> ChangePredictionAsync(string predictionId, PredictionChange change, string? winnerId, CancellationToken ct);
}

public sealed partial class TwitchClient
{
    public Task<ChannelVote?> GetLatestPollAsync(CancellationToken ct) =>
        Settings.HasPollScope ? GetLatestVoteAsync(_endpoints.PollsUrl, VoteParsing.ParsePolls, ct) : Task.FromResult<ChannelVote?>(null);

    public Task<ChannelVote?> GetLatestPredictionAsync(CancellationToken ct) =>
        Settings.HasPredictionScope ? GetLatestVoteAsync(_endpoints.PredictionsUrl, VoteParsing.ParsePredictions, ct) : Task.FromResult<ChannelVote?>(null);

    public async Task<ChannelVote?> GetLastEndedAsync(VoteKind kind, CancellationToken ct)
    {
        var poll = kind == VoteKind.Poll;
        RequireVoteScope(poll ? Settings.HasPollScope : Settings.HasPredictionScope, poll ? "опросы" : "предикты");
        var url = $"{(poll ? _endpoints.PollsUrl : _endpoints.PredictionsUrl)}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&first=10";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        switch (status)
        {
            case HttpStatusCode.OK:
                var votes = poll ? VoteParsing.ParsePolls(body) : VoteParsing.ParsePredictions(body);
                return votes.FirstOrDefault(v => v.Stage == VoteStage.Ended);
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.NotFound:
                return null;
            case HttpStatusCode.Unauthorized:
                throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты).");
            default:
                throw new HttpRequestException($"Twitch: HTTP {(int)status}. {ReadMessage(body)}".Trim());
        }
    }

    public Task<VoteResult> CreatePollAsync(PollDraft draft, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPollScope, "опросы");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["title"] = draft.Title,
            ["choices"] = draft.Choices.Select(title => new Dictionary<string, string> { ["title"] = title }).ToList(),
            ["duration"] = draft.Seconds,
        };
        if (draft.PointsVoting)
        {
            body["channel_points_voting_enabled"] = true;
            body["channel_points_per_vote"] = draft.PointsPerVote;
        }
        return SendVoteAsync(HttpMethod.Post, _endpoints.PollsUrl, body, VoteParsing.ParsePolls, ct);
    }

    public Task<VoteResult> EndPollAsync(string pollId, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPollScope, "опросы");
        var body = new Dictionary<string, object> { ["broadcaster_id"] = Settings.TwitchUserId, ["id"] = pollId, ["status"] = "TERMINATED" };
        return SendVoteAsync(HttpMethod.Patch, _endpoints.PollsUrl, body, VoteParsing.ParsePolls, ct);
    }

    public Task<VoteResult> CreatePredictionAsync(PredictionDraft draft, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPredictionScope, "предикты");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["title"] = draft.Title,
            ["outcomes"] = draft.Outcomes.Select(title => new Dictionary<string, string> { ["title"] = title }).ToList(),
            ["prediction_window"] = draft.Seconds,
        };
        return SendVoteAsync(HttpMethod.Post, _endpoints.PredictionsUrl, body, VoteParsing.ParsePredictions, ct);
    }

    public Task<VoteResult> ChangePredictionAsync(string predictionId, PredictionChange change, string? winnerId, CancellationToken ct)
    {
        RequireVoteScope(Settings.HasPredictionScope, "предикты");
        var body = new Dictionary<string, object>
        {
            ["broadcaster_id"] = Settings.TwitchUserId,
            ["id"] = predictionId,
            ["status"] = change switch
            {
                PredictionChange.Lock => "LOCKED",
                PredictionChange.Resolve => "RESOLVED",
                _ => "CANCELED",
            },
        };
        if (change == PredictionChange.Resolve) body["winning_outcome_id"] = winnerId ?? "";
        return SendVoteAsync(HttpMethod.Patch, _endpoints.PredictionsUrl, body, VoteParsing.ParsePredictions, ct);
    }

    private static void RequireVoteScope(bool granted, string what)
    {
        if (!granted) throw new AuthRequiredException($"Переподключи Twitch в настройках, раздел «Подключения»: нужно право на {what}.");
    }

    private async Task<ChannelVote?> GetLatestVoteAsync(string baseUrl, Func<string, IReadOnlyList<ChannelVote>> parse, CancellationToken ct)
    {
        var url = $"{baseUrl}?broadcaster_id={Uri.EscapeDataString(Settings.TwitchUserId)}&first=1";
        var (status, body) = await CallWithRefreshAsync((token, c) => GetAsync(url, token, c), ct);
        switch (status)
        {
            case HttpStatusCode.OK:
                return parse(body).FirstOrDefault();
            case HttpStatusCode.Forbidden:
            case HttpStatusCode.NotFound:
                return null;
            case HttpStatusCode.Unauthorized:
                throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты).");
            default:
                throw new HttpRequestException($"Twitch: HTTP {(int)status}. {ReadMessage(body)}".Trim());
        }
    }

    private async Task<VoteResult> SendVoteAsync(
        HttpMethod method, string url, Dictionary<string, object> body, Func<string, IReadOnlyList<ChannelVote>> parse, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var (status, response) = await CallWithRefreshAsync((token, c) => SendJsonAsync(method, url, token, json, c), ct);
        var message = ReadMessage(response);
        return status switch
        {
            HttpStatusCode.OK => new VoteResult(true, "", parse(response).FirstOrDefault()),
            HttpStatusCode.BadRequest => new VoteResult(false, $"Twitch не принял: {message}".Trim()),
            HttpStatusCode.Forbidden => new VoteResult(false, "Twitch отказал: опросы и предикты есть только у компаньонов и партнёров Twitch."),
            HttpStatusCode.NotFound => new VoteResult(false, "Этого уже нет на канале."),
            HttpStatusCode.Unauthorized => throw new AuthRequiredException("Twitch отклонил вход: переподключи Twitch в настройках (нужны права на опросы и предикты)."),
            (HttpStatusCode)429 => new VoteResult(false, "Twitch: слишком много запросов, попробуй через минуту."),
            _ => new VoteResult(false, $"Twitch: HTTP {(int)status}. {message}".Trim()),
        };
    }
}
