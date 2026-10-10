using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class VoteParsingTests
{
    internal const string PollJson = """
        {"data":[{"id":"ed961efd","broadcaster_id":"141981764","title":"Heads or Tails?",
          "choices":[{"id":"c1","title":"Heads","votes":3,"channel_points_votes":1,"bits_votes":0},
                     {"id":"c2","title":"Tails","votes":1,"channel_points_votes":0,"bits_votes":0}],
          "channel_points_voting_enabled":true,"channel_points_per_vote":100,"status":"ACTIVE","duration":1800,
          "started_at":"2021-03-19T06:08:33.871278372Z"}],"pagination":{}}
        """;

    internal const string PredictionJson = """
        {"data":[{"id":"bc637af0","broadcaster_id":"141981764","title":"Any leeks in the stream?","winning_outcome_id":null,
          "outcomes":[{"id":"o1","title":"Yes, give it time.","users":7,"channel_points":1250,"top_predictors":null,"color":"BLUE"},
                      {"id":"o2","title":"Definitely not.","users":3,"channel_points":750,"top_predictors":null,"color":"PINK"}],
          "prediction_window":120,"status":"ACTIVE","created_at":"2021-04-28T17:11:22.595914172Z","ended_at":null,"locked_at":null}],"pagination":{}}
        """;

    [Fact]
    public void A_poll_from_Twitch_gives_choices_votes_and_the_end_time()
    {
        var poll = Assert.Single(VoteParsing.ParsePolls(PollJson));

        Assert.Equal(VoteKind.Poll, poll.Kind);
        Assert.Equal("ed961efd", poll.Id);
        Assert.Equal("Heads or Tails?", poll.Title);
        Assert.Equal(VoteStage.Active, poll.Stage);
        Assert.Equal(new[] { "Heads", "Tails" }, poll.Options.Select(o => o.Title));
        Assert.Equal(new long[] { 3, 1 }, poll.Options.Select(o => o.Votes));
        Assert.Equal(new DateTime(2021, 3, 19, 6, 8, 33, DateTimeKind.Utc), poll.StartedUtc.AddTicks(-(poll.StartedUtc.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(poll.StartedUtc.AddMinutes(30), poll.EndsUtc);
        Assert.Null(poll.EndedUtc);
    }

    [Fact]
    public void A_prediction_from_Twitch_gives_outcomes_points_people_and_colours()
    {
        var prediction = Assert.Single(VoteParsing.ParsePredictions(PredictionJson));

        Assert.Equal(VoteKind.Prediction, prediction.Kind);
        Assert.Equal(VoteStage.Active, prediction.Stage);
        Assert.Equal(new long[] { 1250, 750 }, prediction.Options.Select(o => o.Points));
        Assert.Equal(new long[] { 7, 3 }, prediction.Options.Select(o => o.Users));
        Assert.Equal(new[] { "BLUE", "PINK" }, prediction.Options.Select(o => o.Color));
        Assert.Equal(prediction.StartedUtc.AddMinutes(2), prediction.EndsUtc);
        Assert.Null(prediction.WinnerId);
    }

    [Theory]
    [InlineData("ACTIVE", VoteStage.Active)]
    [InlineData("COMPLETED", VoteStage.Ended)]
    [InlineData("TERMINATED", VoteStage.Ended)]
    public void Poll_statuses_map_to_stages(string status, VoteStage stage)
    {
        Assert.Equal(stage, Assert.Single(VoteParsing.ParsePolls(PollJson.Replace("\"ACTIVE\"", $"\"{status}\""))).Stage);
    }

    [Theory]
    [InlineData("ARCHIVED")]
    [InlineData("MODERATED")]
    [InlineData("INVALID")]
    public void Hidden_or_broken_polls_are_skipped(string status)
    {
        Assert.Empty(VoteParsing.ParsePolls(PollJson.Replace("\"ACTIVE\"", $"\"{status}\"")));
    }

    [Fact]
    public void A_resolved_prediction_knows_its_winner_and_end()
    {
        var json = PredictionJson.Replace("\"ACTIVE\"", "\"RESOLVED\"").Replace("\"winning_outcome_id\":null", "\"winning_outcome_id\":\"o2\"")
            .Replace("\"ended_at\":null", "\"ended_at\":\"2021-04-28T17:14:00Z\"");

        var prediction = Assert.Single(VoteParsing.ParsePredictions(json));

        Assert.Equal(VoteStage.Ended, prediction.Stage);
        Assert.Equal("o2", prediction.WinnerId);
        Assert.Equal(new DateTime(2021, 4, 28, 17, 14, 0, DateTimeKind.Utc), prediction.EndedUtc);
        Assert.False(prediction.IsCanceled);
    }

    [Theory]
    [InlineData("LOCKED", VoteStage.Locked, false)]
    [InlineData("CANCELED", VoteStage.Ended, true)]
    public void Locked_and_canceled_predictions(string status, VoteStage stage, bool canceled)
    {
        var prediction = Assert.Single(VoteParsing.ParsePredictions(PredictionJson.Replace("\"ACTIVE\"", $"\"{status}\"")));

        Assert.Equal(stage, prediction.Stage);
        Assert.Equal(canceled, prediction.IsCanceled);
    }

    [Fact]
    public void An_empty_answer_means_nothing_is_running()
    {
        Assert.Empty(VoteParsing.ParsePolls("{\"data\":[],\"pagination\":{}}"));
        Assert.Empty(VoteParsing.ParsePredictions("{}"));
    }
}

public class VoteRulesTests
{
    [Fact]
    public void A_good_poll_is_trimmed_and_empty_choices_are_dropped()
    {
        var (draft, problem) = VoteRules.Poll("  Что играем?  ", new[] { " Hollow Knight ", "", "Silksong", "  " }, 60, false, 100);

        Assert.Null(problem);
        Assert.Equal("Что играем?", draft!.Title);
        Assert.Equal(new[] { "Hollow Knight", "Silksong" }, draft.Choices);
        Assert.Equal(60, draft.Seconds);
        Assert.False(draft.PointsVoting);
        Assert.Equal(0, draft.PointsPerVote);
    }

    [Theory]
    [InlineData("", "a|b", 60, "Напиши вопрос.")]
    [InlineData("x", "a", 60, "Нужно хотя бы 2 варианта.")]
    [InlineData("x", "a|b|c|d|e|f", 60, "Не больше 5 вариантов.")]
    [InlineData("x", "a|A", 60, "Варианты не должны повторяться.")]
    [InlineData("x", "a|b", 14, "Время от 15 секунд до 30 минут.")]
    [InlineData("x", "a|b", 1801, "Время от 15 секунд до 30 минут.")]
    public void Bad_polls_are_explained(string title, string choices, int seconds, string expected)
    {
        Assert.Equal(expected, VoteRules.Poll(title, choices.Split('|'), seconds, false, 0).Problem);
    }

    [Fact]
    public void Long_texts_are_refused_with_the_limit()
    {
        Assert.Equal("Вопрос не длиннее 60 знаков.", VoteRules.Poll(new string('я', 61), new[] { "a", "b" }, 60, false, 0).Problem);
        Assert.Equal("Вопрос не длиннее 45 знаков.", VoteRules.Prediction(new string('я', 46), new[] { "a", "b" }, 60).Problem);
        Assert.Equal($"«{new string('б', 26)}» длиннее 25 знаков.", VoteRules.Poll("x", new[] { "a", new string('б', 26) }, 60, false, 0).Problem);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1_000_000, true)]
    [InlineData(1_000_001, false)]
    public void Points_per_vote_must_be_in_Twitchs_range(int perVote, bool ok)
    {
        var (draft, problem) = VoteRules.Poll("x", new[] { "a", "b" }, 60, true, perVote);

        Assert.Equal(ok, problem == null);
        if (ok) Assert.Equal(perVote, draft!.PointsPerVote);
    }

    [Fact]
    public void Predictions_allow_ten_outcomes_and_need_thirty_seconds()
    {
        var ten = Enumerable.Range(1, 10).Select(i => "исход " + i).ToList();

        Assert.Null(VoteRules.Prediction("Кто победит?", ten, 30).Problem);
        Assert.Equal("Не больше 10 исходов.", VoteRules.Prediction("x", ten.Append("ещё").ToList(), 30).Problem);
        Assert.Equal("Нужно хотя бы 2 исхода.", VoteRules.Prediction("x", new[] { "один" }, 30).Problem);
        Assert.Equal("Время от 30 секунд до 30 минут.", VoteRules.Prediction("x", new[] { "a", "b" }, 29).Problem);
        Assert.Equal("Исходы не должны повторяться.", VoteRules.Prediction("x", new[] { "Да", "да" }, 60).Problem);
    }
}

public class VoteCardTests
{
    private static readonly DateTime Start = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static ChannelVote Poll(VoteStage stage = VoteStage.Active, long a = 3, long b = 1, DateTime? ended = null, string status = "ACTIVE") =>
        new(VoteKind.Poll, "p1", "Что играем?", stage, status,
            new[] { new VoteOption("c1", "Hollow Knight", a, 0, 0, ""), new VoteOption("c2", "Silksong", b, 0, 0, "") },
            Start, Start.AddMinutes(2), ended, null);

    private static ChannelVote Prediction(VoteStage stage = VoteStage.Active, string status = "ACTIVE", string? winner = null, DateTime? ended = null) =>
        new(VoteKind.Prediction, "r1", "Победит босса?", stage, status,
            new[] { new VoteOption("o1", "Да", 7, 1250, 7, "BLUE"), new VoteOption("o2", "Нет", 3, 750, 3, "PINK") },
            Start, Start.AddMinutes(2), ended, winner);

    [Fact]
    public void Headers_name_the_kind()
    {
        Assert.Equal("Опрос: Что играем?", VoteCardText.Header(Poll()));
        Assert.Equal("Предикт: Победит босса?", VoteCardText.Header(Prediction()));
    }

    [Fact]
    public void The_status_counts_down_and_then_says_what_happens()
    {
        Assert.Equal("осталось 1:23", VoteCardText.Status(Poll(), Start.AddSeconds(37)));
        Assert.Equal("осталось 1:24", VoteCardText.Status(Poll(), Start.AddSeconds(36.5)));
        Assert.Equal("ставки ещё 2:00", VoteCardText.Status(Prediction(), Start));
        Assert.Equal("подводим итог…", VoteCardText.Status(Poll(), Start.AddMinutes(3)));
        Assert.Equal("Ставки закрыты, выбери победителя", VoteCardText.Status(Prediction(VoteStage.Locked, "LOCKED"), Start));
        Assert.Equal("Итог", VoteCardText.Status(Poll(VoteStage.Ended, status: "COMPLETED"), Start));
        Assert.Equal("Отменён, баллы возвращены", VoteCardText.Status(Prediction(VoteStage.Ended, "CANCELED"), Start));
    }

    [Fact]
    public void Shares_come_from_votes_for_polls_and_points_for_predictions()
    {
        Assert.Equal(new[] { 75, 25 }, VoteCardText.Percents(Poll()));
        Assert.Equal(new[] { 63, 38 }, VoteCardText.Percents(Prediction()));
        Assert.Equal(new[] { 0, 0 }, VoteCardText.Percents(Poll(a: 0, b: 0)));
    }

    [Fact]
    public void Details_use_the_right_word_forms()
    {
        var poll = Poll(a: 21, b: 12);

        Assert.Equal("21 голос · 64%", VoteCardText.Detail(poll, poll.Options[0], 64));
        Assert.Equal("12 голосов · 36%", VoteCardText.Detail(poll, poll.Options[1], 36));
        Assert.Equal("3 голоса · 75%", VoteCardText.Detail(Poll(), Poll().Options[0], 75));
        var prediction = Prediction();
        Assert.Equal($"{Redemption.FormatCost(1250)} · 7 чел. · 63%", VoteCardText.Detail(prediction, prediction.Options[0], 63));
    }

    [Fact]
    public void Winners_are_the_most_voted_choices_or_the_chosen_outcome()
    {
        Assert.Empty(VoteCardText.Winners(Poll()));
        Assert.Equal(new[] { "c1" }, VoteCardText.Winners(Poll(VoteStage.Ended, status: "COMPLETED")));
        Assert.Equal(new[] { "c1", "c2" }, VoteCardText.Winners(Poll(VoteStage.Ended, a: 2, b: 2, status: "COMPLETED")).OrderBy(x => x));
        Assert.Empty(VoteCardText.Winners(Poll(VoteStage.Ended, a: 0, b: 0, status: "COMPLETED")));
        Assert.Equal(new[] { "o2" }, VoteCardText.Winners(Prediction(VoteStage.Ended, "RESOLVED", "o2")));
        Assert.Empty(VoteCardText.Winners(Prediction(VoteStage.Ended, "CANCELED")));
    }

    [Fact]
    public void Ended_cards_stay_for_a_short_while_only()
    {
        var ended = Poll(VoteStage.Ended, ended: Start, status: "COMPLETED");

        Assert.True(VoteCardText.StillShown(Poll(), Start.AddHours(5)));
        Assert.True(VoteCardText.StillShown(ended, Start.AddSeconds(14)));
        Assert.False(VoteCardText.StillShown(ended, Start.AddSeconds(16)));
        Assert.False(VoteCardText.StillShown(Poll(VoteStage.Ended, status: "COMPLETED"), Start));
        Assert.False(VoteCardText.StillShown(null, Start));
    }

    [Fact]
    public void Buttons_follow_the_stage_and_appear_only_in_mouse_mode()
    {
        var card = new VoteCardModel(Prediction(), Start, interactive: false);
        Assert.False(card.ShowButtons);

        card.SetInteractive(true, Start);
        Assert.True(card.ShowLock);
        Assert.True(card.ShowCancel);
        Assert.False(card.ShowEnd);
        Assert.All(card.Rows, r => Assert.False(r.ShowWin));

        card.Update(Prediction(VoteStage.Locked, "LOCKED"), Start, true);
        Assert.False(card.ShowLock);
        Assert.True(card.ShowCancel);
        Assert.All(card.Rows, r => Assert.True(r.ShowWin));

        card.Update(Prediction(VoteStage.Ended, "RESOLVED", "o1", Start), Start, true);
        Assert.False(card.ShowButtons);
        Assert.Equal("✓ Да", card.Rows[0].Label);
        Assert.Equal(1, card.Rows[0].RowOpacity);
        Assert.True(card.Rows[1].RowOpacity < 1);
    }

    [Fact]
    public void A_poll_card_offers_ending_it_and_updates_rows_in_place()
    {
        var card = new VoteCardModel(Poll(), Start, interactive: true);
        var first = card.Rows[0];

        card.Update(Poll(a: 10, b: 30), Start.AddSeconds(5), true);

        Assert.True(card.ShowEnd);
        Assert.Same(first, card.Rows[0]);
        Assert.Equal(25, card.Rows[0].Percent);
        Assert.Equal("30 голосов · 75%", card.Rows[1].Detail);
    }

    [Fact]
    public void Confirmations_are_cleared_when_the_stage_changes_or_mouse_mode_ends()
    {
        var card = new VoteCardModel(Prediction(VoteStage.Locked, "LOCKED"), Start, interactive: true);
        card.CancelArmed = true;
        card.Rows[0].Armed = true;

        Assert.Equal("Точно? Отменить", card.CancelText);
        Assert.Equal("Точно?", card.Rows[0].WinText);
        card.SetInteractive(false, Start);

        Assert.False(card.CancelArmed);
        Assert.False(card.Rows[0].Armed);
        Assert.Equal("Победил «Да»", card.Rows[0].WinName);
    }
}

internal sealed class FakeVoteApi : IVoteApi
{
    public ChannelVote? LatestPoll { get; set; }
    public ChannelVote? LatestPrediction { get; set; }
    public int PollReads { get; private set; }
    public int PredictionReads { get; private set; }

    public Task<ChannelVote?> GetLatestPollAsync(CancellationToken ct)
    {
        PollReads++;
        return Task.FromResult(LatestPoll);
    }

    public Task<ChannelVote?> GetLatestPredictionAsync(CancellationToken ct)
    {
        PredictionReads++;
        return Task.FromResult(LatestPrediction);
    }

    public Task<ChannelVote?> GetLastEndedAsync(VoteKind kind, CancellationToken ct) => Task.FromResult<ChannelVote?>(null);

    public Task<VoteResult> CreatePollAsync(PollDraft draft, CancellationToken ct) => Task.FromResult(new VoteResult(true, ""));

    public Task<VoteResult> EndPollAsync(string pollId, CancellationToken ct) => Task.FromResult(new VoteResult(true, ""));

    public Task<VoteResult> CreatePredictionAsync(PredictionDraft draft, CancellationToken ct) => Task.FromResult(new VoteResult(true, ""));

    public Task<VoteResult> ChangePredictionAsync(string predictionId, PredictionChange change, string? winnerId, CancellationToken ct) =>
        Task.FromResult(new VoteResult(true, ""));
}

public class VoteWatcherTests
{
    private static SettingsStore Store(TempDir dir, string scopes)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.TwitchRefreshToken = "r";
        store.Current.TwitchUserId = "777";
        store.Current.TwitchScopes = scopes;
        return store;
    }

    private static ChannelVote Active(VoteKind kind) =>
        new(kind, kind + "1", "x", VoteStage.Active, "ACTIVE", Array.Empty<VoteOption>(), DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), null, null);

    [Fact]
    public async Task Without_the_rights_nothing_is_asked()
    {
        using var dir = new TempDir();
        var api = new FakeVoteApi();
        var watcher = new VoteWatcher(Store(dir, "user:read:chat"), api);

        Assert.False(await watcher.PollOnceAsync(CancellationToken.None));
        Assert.Equal(0, api.PollReads + api.PredictionReads);
    }

    [Fact]
    public async Task Only_the_granted_kind_is_asked_and_changes_are_announced_once()
    {
        using var dir = new TempDir();
        var api = new FakeVoteApi { LatestPoll = Active(VoteKind.Poll) };
        var watcher = new VoteWatcher(Store(dir, "channel:manage:polls"), api);
        var changes = 0;
        watcher.Changed += () => changes++;

        Assert.True(await watcher.PollOnceAsync(CancellationToken.None));
        await watcher.PollOnceAsync(CancellationToken.None);

        Assert.Equal(2, api.PollReads);
        Assert.Equal(0, api.PredictionReads);
        Assert.Equal(1, changes);
        Assert.True(watcher.IsBusy(VoteKind.Poll));
        Assert.False(watcher.IsBusy(VoteKind.Prediction));
    }

    [Fact]
    public void A_vote_returned_by_Twitch_shows_up_at_once()
    {
        using var dir = new TempDir();
        var watcher = new VoteWatcher(Store(dir, "channel:manage:predictions"), new FakeVoteApi());
        var changes = 0;
        watcher.Changed += () => changes++;

        watcher.Show(Active(VoteKind.Prediction));

        Assert.Equal(1, changes);
        Assert.Equal("Prediction1", watcher.Prediction!.Id);
        Assert.Null(watcher.Poll);
    }
}

public class VoteRequestTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "channel:manage:polls channel:manage:predictions")
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.TwitchAccessToken = "tw-access-1";
        store.Current.TwitchRefreshToken = "tw-refresh-1";
        store.Current.TwitchAccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
        store.Current.TwitchUserId = "777";
        store.Current.TwitchScopes = scopes;
        return store;
    }

    [Fact]
    public async Task Creating_a_poll_sends_the_question_choices_time_and_points()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        string? body = null;
        da.Handler = (_, b) =>
        {
            body = b;
            return (200, VoteParsingTests.PollJson);
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.CreatePollAsync(new PollDraft("Что играем?", new[] { "HK", "Silksong" }, 90, true, 50), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("ed961efd", result.Vote!.Id);
        Assert.Contains("POST /helix/polls", Assert.Single(da.Requests));
        var json = System.Text.Json.JsonDocument.Parse(body!).RootElement;
        Assert.Equal("777", json.GetProperty("broadcaster_id").GetString());
        Assert.Equal("Что играем?", json.GetProperty("title").GetString());
        Assert.Equal(new[] { "HK", "Silksong" }, json.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("title").GetString()));
        Assert.Equal(90, json.GetProperty("duration").GetInt32());
        Assert.True(json.GetProperty("channel_points_voting_enabled").GetBoolean());
        Assert.Equal(50, json.GetProperty("channel_points_per_vote").GetInt32());
    }

    [Fact]
    public async Task Ending_a_poll_and_settling_a_prediction_patch_the_status()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var bodies = new List<string>();
        da.Handler = (req, b) =>
        {
            bodies.Add(req.HttpMethod + " " + req.Url!.AbsolutePath + " " + b);
            return (200, req.Url.AbsolutePath.EndsWith("polls") ? VoteParsingTests.PollJson : VoteParsingTests.PredictionJson);
        };
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await client.EndPollAsync("p1", CancellationToken.None);
        await client.ChangePredictionAsync("r1", PredictionChange.Lock, null, CancellationToken.None);
        await client.ChangePredictionAsync("r1", PredictionChange.Resolve, "o2", CancellationToken.None);
        await client.ChangePredictionAsync("r1", PredictionChange.Cancel, null, CancellationToken.None);

        Assert.Equal("PATCH /helix/polls {\"broadcaster_id\":\"777\",\"id\":\"p1\",\"status\":\"TERMINATED\"}", bodies[0]);
        Assert.Equal("PATCH /helix/predictions {\"broadcaster_id\":\"777\",\"id\":\"r1\",\"status\":\"LOCKED\"}", bodies[1]);
        Assert.Equal("PATCH /helix/predictions {\"broadcaster_id\":\"777\",\"id\":\"r1\",\"status\":\"RESOLVED\",\"winning_outcome_id\":\"o2\"}", bodies[2]);
        Assert.Equal("PATCH /helix/predictions {\"broadcaster_id\":\"777\",\"id\":\"r1\",\"status\":\"CANCELED\"}", bodies[3]);
    }

    [Theory]
    [InlineData(400, "{\"message\":\"poll already active\"}", "Twitch не принял: poll already active")]
    [InlineData(403, "{}", "только у компаньонов и партнёров")]
    [InlineData(404, "{}", "Этого уже нет на канале.")]
    [InlineData(500, "{\"message\":\"boom\"}", "HTTP 500")]
    public async Task Refusals_are_explained(int status, string response, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, response);
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.CreatePredictionAsync(new PredictionDraft("x", new[] { "a", "b" }, 60), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(expected, result.Message);
    }

    [Fact]
    public async Task Reading_asks_for_the_latest_one_and_a_non_affiliate_channel_is_quietly_empty()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (req, _) => req.Url!.AbsolutePath.EndsWith("polls") ? (200, VoteParsingTests.PollJson) : (403, "{\"message\":\"not affiliate\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var poll = await client.GetLatestPollAsync(CancellationToken.None);
        var prediction = await client.GetLatestPredictionAsync(CancellationToken.None);

        Assert.Equal("ed961efd", poll!.Id);
        Assert.Null(prediction);
        Assert.Contains("GET /helix/polls?broadcaster_id=777&first=1", da.Requests[0]);
    }

    [Fact]
    public async Task The_last_result_is_the_newest_finished_one_not_the_running_one()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var running = VoteParsingTests.PollJson.Replace("\"id\":\"ed961efd\"", "\"id\":\"now\"");
        var finished = VoteParsingTests.PollJson.Replace("\"ACTIVE\"", "\"COMPLETED\"").Replace("\"id\":\"ed961efd\"", "\"id\":\"before\"");
        var both = running[..running.LastIndexOf("}],", StringComparison.Ordinal)] + "}," + finished[(finished.IndexOf("[{", StringComparison.Ordinal) + 1)..];
        da.Handler = (req, _) => req.Url!.AbsolutePath.EndsWith("polls") ? (200, both) : (200, "{\"data\":[]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var poll = await client.GetLastEndedAsync(VoteKind.Poll, CancellationToken.None);
        var prediction = await client.GetLastEndedAsync(VoteKind.Prediction, CancellationToken.None);

        Assert.Equal("before", poll!.Id);
        Assert.Equal(VoteStage.Ended, poll.Stage);
        Assert.Null(prediction);
        Assert.Contains("GET /helix/polls?broadcaster_id=777&first=10", da.Requests[0]);
    }

    [Fact]
    public async Task Without_the_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        await Assert.ThrowsAsync<AuthRequiredException>(() => client.CreatePollAsync(new PollDraft("x", new[] { "a", "b" }, 60, false, 0), CancellationToken.None));
        Assert.Null(await client.GetLatestPollAsync(CancellationToken.None));
        Assert.Empty(da.Requests);
    }
}
