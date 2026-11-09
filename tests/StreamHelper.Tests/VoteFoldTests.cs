using System.Windows;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class VoteFoldTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);

    private static ChannelVote Poll(VoteStage stage, params long[] votes) =>
        new(VoteKind.Poll, "p1", "Во что играем дальше?", stage, stage == VoteStage.Ended ? "COMPLETED" : "ACTIVE",
            votes.Select((v, i) => new VoteOption("o" + i, new[] { "Dead by Daylight", "Hollow Knight", "Identity V" }[i], v, 0, v, "")).ToList(),
            Now.AddMinutes(-1), Now.AddSeconds(79), stage == VoteStage.Ended ? Now : null, null);

    private static ChannelVote Prediction(string status, string? winner, params long[] points) =>
        new(VoteKind.Prediction, "r1", "Пройду с первой попытки?", status == "ACTIVE" ? VoteStage.Active : status == "LOCKED" ? VoteStage.Locked : VoteStage.Ended, status,
            points.Select((p, i) => new VoteOption("o" + i, i == 0 ? "Да" : "Нет", 0, p, 1, i == 0 ? "BLUE" : "PINK")).ToList(),
            Now.AddMinutes(-1), Now.AddMinutes(1), status is "ACTIVE" or "LOCKED" ? null : Now, winner);

    [Fact]
    public void A_folded_card_says_who_leads()
    {
        Assert.Equal("Hollow Knight 58%", VoteCardText.Leader(Poll(VoteStage.Active, 5, 7, 0)));
        Assert.Equal("Нет 75%", VoteCardText.Leader(Prediction("ACTIVE", null, 1000, 3000)));
    }

    [Fact]
    public void A_folded_card_shows_the_winner_once_it_is_over()
    {
        Assert.Equal("✓ Dead by Daylight 60%", VoteCardText.Leader(Poll(VoteStage.Ended, 6, 4, 0)));
        Assert.Equal("✓ Да 25%", VoteCardText.Leader(Prediction("RESOLVED", "o0", 1000, 3000)));
    }

    [Fact]
    public void Without_votes_or_after_a_cancel_there_is_no_leader()
    {
        Assert.Equal("", VoteCardText.Leader(Poll(VoteStage.Active, 0, 0, 0)));
        Assert.Equal("", VoteCardText.Leader(Prediction("CANCELED", null, 1000, 3000)));
    }

    [Fact]
    public void A_folded_card_keeps_only_a_short_time()
    {
        Assert.Equal("1:19", VoteCardText.ShortStatus(Poll(VoteStage.Active, 5, 7, 0), Now));
        Assert.Equal("…", VoteCardText.ShortStatus(Poll(VoteStage.Active, 5, 7, 0), Now.AddMinutes(5)));
        Assert.Equal("ставки закрыты", VoteCardText.ShortStatus(Prediction("LOCKED", null, 1, 1), Now));
        Assert.Equal("итог", VoteCardText.ShortStatus(Poll(VoteStage.Ended, 6, 4, 0), Now));
        Assert.Equal("отменён", VoteCardText.ShortStatus(Prediction("CANCELED", null, 1, 1), Now));
    }

    [Fact]
    public void Folding_hides_the_options_and_shows_one_line()
    {
        var card = new VoteCardModel(Poll(VoteStage.Active, 5, 7, 0), Now, interactive: true);
        Assert.True(card.Expanded);
        Assert.False(card.ShowLeader);
        Assert.Equal(("▾", "Свернуть", TextWrapping.Wrap, "осталось 1:19"), (card.FoldGlyph, card.FoldName, card.TitleWrapping, card.ShownStatus));
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Collapsed = true;

        Assert.False(card.Expanded);
        Assert.True(card.ShowLeader);
        Assert.Equal(("▸", "Развернуть", TextWrapping.NoWrap, "1:19"), (card.FoldGlyph, card.FoldName, card.TitleWrapping, card.ShownStatus));
        Assert.Equal(("Опрос: Во что играем дальше?", "Hollow Knight 58%"), (card.Header, card.Leader));
        Assert.True(card.CardPadding.Top < 6);
        Assert.Contains(nameof(VoteCardModel.Expanded), changed);
        Assert.Contains(nameof(VoteCardModel.ShownStatus), changed);
    }

    [Fact]
    public void While_folding_the_options_stay_until_the_animation_lets_them_go()
    {
        var card = new VoteCardModel(Poll(VoteStage.Active, 5, 7, 0), Now, interactive: true);

        card.KeepBody(true);
        card.Collapsed = true;
        Assert.True(card.BodyShown);
        Assert.True(card.ShowLeader);

        card.KeepBody(false);
        Assert.False(card.BodyShown);

        card.Collapsed = false;
        Assert.True(card.BodyShown);
    }

    [Fact]
    public void A_new_card_starts_folded_when_the_last_choice_was_folded_and_follows_new_votes()
    {
        var card = new VoteCardModel(Poll(VoteStage.Active, 5, 7, 0), Now, interactive: false, collapsed: true);
        Assert.True(card.Collapsed);

        card.Update(Poll(VoteStage.Active, 9, 7, 0), Now, interactive: false);
        card.Tick(Now.AddSeconds(10));

        Assert.Equal(("Dead by Daylight 56%", "1:09"), (card.Leader, card.ShownStatus));
        Assert.False(new AppSettings().ChatVotesCollapsed);
        Assert.Contains(nameof(AppSettings.ChatVotesCollapsed), Profile.SharedSettings);
    }
}
