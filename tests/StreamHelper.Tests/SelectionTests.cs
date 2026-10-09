using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class SelectionTrackerTests
{
    private static Redemption Reward(string key, bool manage = true, RedemptionStatus status = RedemptionStatus.Pending) =>
        new() { Key = key, RewardId = "copy", DisplayName = "Viewer", RewardTitle = "Track", CanManage = manage, Status = status };

    [Fact]
    public void It_counts_the_ticked_cards_and_selects_or_clears_all_in_turn()
    {
        using var dir = new TempDir();
        var store = new FollowerStore(dir.File("followers.json"));
        store.AddRange(new[] { MakeFollower.F("a", 1), MakeFollower.F("b", 2), MakeFollower.F("c", 3) });
        var tracker = new SelectionTracker<Follower>(store.Items);
        var changes = new List<string?>();
        tracker.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(tracker.HasAny);
        Assert.Equal("Выбрать все", tracker.ToggleAllText);

        store.Items[1].Selected = true;
        Assert.Equal(1, tracker.Count);
        Assert.True(tracker.HasAny);
        Assert.False(tracker.AllSelected);
        Assert.Equal("Выбрать все", tracker.ToggleAllText);
        Assert.Contains(nameof(tracker.Count), changes);

        tracker.ToggleAll();
        Assert.Equal(3, tracker.Count);
        Assert.True(tracker.AllSelected);
        Assert.Equal("Снять выбор", tracker.ToggleAllText);

        tracker.ToggleAll();
        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.HasAny);
    }

    [Fact]
    public void Cards_that_arrive_or_leave_change_the_count_and_a_removed_card_no_longer_counts()
    {
        using var dir = new TempDir();
        var store = new FollowerStore(dir.File("followers.json"));
        store.AddRange(new[] { MakeFollower.F("a", 1), MakeFollower.F("b", 2) });
        var tracker = new SelectionTracker<Follower>(store.Items);
        tracker.ToggleAll();
        Assert.True(tracker.AllSelected);

        store.AddRange(new[] { MakeFollower.F("c", 3) });
        Assert.False(tracker.AllSelected);
        Assert.Equal(2, tracker.Count);

        store.Remove(store.Items[0]);
        Assert.Equal(2, tracker.Count);
        Assert.Equal(2, tracker.Picked().Count);
    }

    [Fact]
    public void Only_rewards_the_program_can_still_decide_can_be_ticked()
    {
        var mine = Reward("mine");
        var foreign = Reward("foreign", manage: false);
        var done = Reward("done", status: RedemptionStatus.Accepted);
        var items = new System.Collections.ObjectModel.ObservableCollection<Redemption> { mine, foreign, done };
        var tracker = new SelectionTracker<Redemption>(items);

        tracker.ToggleAll();

        Assert.True(mine.Selected);
        Assert.False(foreign.Selected);
        Assert.False(done.Selected);
        Assert.True(tracker.AllSelected);
        Assert.Equal(1, tracker.Count);

        foreign.Selected = true;
        Assert.Equal(1, tracker.Count);
        Assert.Same(mine, Assert.Single(tracker.Picked()));
    }

    [Fact]
    public void A_ticked_reward_loses_its_tick_when_it_can_no_longer_be_decided_and_the_tick_is_not_saved()
    {
        using var dir = new TempDir();
        var path = dir.File("redemptions.json");
        var store = new RedemptionStore(path);
        store.AddRange(new[] { Reward("a") });
        var tracker = new SelectionTracker<Redemption>(store.Items);
        store.Items[0].Selected = true;
        Assert.Equal(1, tracker.Count);
        Assert.DoesNotContain("Selected", System.IO.File.ReadAllText(path));

        store.Items[0].Status = RedemptionStatus.Processed;

        Assert.False(store.Items[0].Selected);
        Assert.Equal(0, tracker.Count);
        Assert.False(tracker.HasAny);
    }
}

public class BulkRedemptionDecisionTests
{
    private static Redemption Reward(string key) =>
        new() { Key = key, RewardId = "copy", DisplayName = "Viewer", RewardTitle = "Track", CanManage = true };

    [Fact]
    public async Task Accepting_several_rewards_marks_each_one_and_hands_it_to_the_list()
    {
        var api = new FakeRewardApi();
        var rewards = new[] { Reward("a"), Reward("b"), Reward("c") };
        foreach (var r in rewards) r.Selected = true;
        var resolved = new List<string>();

        var result = await RedemptionDecisions.DecideManyAsync(api, rewards, RedemptionDecision.Fulfilled, r => resolved.Add(r.Key), CancellationToken.None);

        Assert.Equal(new BulkDecisionResult(3, 0, 0, null), result);
        Assert.Equal(new[] { "a", "b", "c" }, resolved);
        Assert.All(rewards, r =>
        {
            Assert.Equal(RedemptionStatus.Accepted, r.Status);
            Assert.True(r.Done);
            Assert.False(r.Selected);
            Assert.False(r.IsBusy);
        });
        Assert.All(api.Updates, u => Assert.Equal(RedemptionDecision.Fulfilled, u.Decision));
        Assert.Equal("Принято: 3", result.Summary(RedemptionDecision.Fulfilled));
    }

    [Fact]
    public async Task A_mixed_batch_counts_decided_settled_and_failed_and_keeps_the_failed_ones_ticked()
    {
        var api = new FakeRewardApi
        {
            OnUpdate = id => id switch
            {
                "a" => new RedemptionUpdateResult(RedemptionUpdateOutcome.Done, ""),
                "b" => new RedemptionUpdateResult(RedemptionUpdateOutcome.AlreadyProcessed, ""),
                _ => new RedemptionUpdateResult(RedemptionUpdateOutcome.Failed, "Twitch не отвечает"),
            },
        };
        var rewards = new[] { Reward("a"), Reward("b"), Reward("c") };
        foreach (var r in rewards) r.Selected = true;
        var resolved = new List<string>();

        var result = await RedemptionDecisions.DecideManyAsync(api, rewards, RedemptionDecision.Canceled, r => resolved.Add(r.Key), CancellationToken.None);

        Assert.Equal(1, result.Done);
        Assert.Equal(1, result.Settled);
        Assert.Equal(1, result.Failed);
        Assert.Equal(new[] { "a", "b" }, resolved);
        Assert.Equal(RedemptionStatus.Rejected, rewards[0].Status);
        Assert.Equal(RedemptionStatus.Processed, rewards[1].Status);
        Assert.Equal(RedemptionStatus.Pending, rewards[2].Status);
        Assert.True(rewards[2].Selected);
        Assert.Equal(
            "Отклонено, баллы возвращены: 1, уже обработано в Twitch: 1, не получилось: 1 (Не получилось: Twitch не отвечает)",
            result.Summary(RedemptionDecision.Canceled));
    }

    [Fact]
    public async Task A_lost_connection_stops_the_batch_and_leaves_the_rest_ticked()
    {
        var api = new FakeRewardApi
        {
            OnUpdate = id => id == "b" ? throw new HttpRequestException("network down") : new RedemptionUpdateResult(RedemptionUpdateOutcome.Done, ""),
        };
        var rewards = new[] { Reward("a"), Reward("b"), Reward("c") };
        foreach (var r in rewards) r.Selected = true;

        var result = await RedemptionDecisions.DecideManyAsync(api, rewards, RedemptionDecision.Fulfilled, _ => { }, CancellationToken.None);

        Assert.Equal(1, result.Done);
        Assert.Equal(1, result.Failed);
        Assert.Equal("network down", result.Problem);
        Assert.Equal(2, api.Updates.Count);
        Assert.True(rewards[1].Selected);
        Assert.True(rewards[2].Selected);
        Assert.All(rewards, r => Assert.False(r.IsBusy));
    }

    [Fact]
    public async Task A_card_that_is_already_being_decided_is_left_alone()
    {
        var api = new FakeRewardApi();
        var busy = Reward("a");
        busy.IsBusy = true;

        var result = await RedemptionDecisions.DecideManyAsync(api, new[] { busy }, RedemptionDecision.Fulfilled, _ => { }, CancellationToken.None);

        Assert.Empty(api.Updates);
        Assert.Equal("Нечего обрабатывать.", result.Summary(RedemptionDecision.Fulfilled));
    }
}

public class BulkDeleteViewModelTests
{
    [Fact]
    public void Ticked_followers_are_deleted_together_and_one_undo_puts_them_back_in_place()
    {
        using var dir = new TempDir();
        var followers = new FollowerStore(dir.File("followers.json"));
        followers.AddRange(new[] { MakeFollower.F("a", 1), MakeFollower.F("b", 2), MakeFollower.F("c", 3), MakeFollower.F("d", 4) });
        var order = followers.Items.Select(f => f.UserId).ToArray();
        var viewModel = new MainViewModel(
            new DonationStore(dir.File("d.json")), followers, new SubscriberStore(dir.File("s.json")),
            new RedemptionStore(dir.File("r.json")), new PingStore(dir.File("p.json")), 1);
        followers.Items[0].Selected = true;
        followers.Items[2].Selected = true;

        viewModel.DeleteSelectedFollowers();

        Assert.Equal(new[] { order[1], order[3] }, followers.Items.Select(f => f.UserId).ToArray());
        Assert.False(viewModel.FollowerSelection.HasAny);
        Assert.Equal("Удалено фолловеров: 2", viewModel.UndoText);
        Assert.True(viewModel.CanUndo);

        viewModel.Undo();

        Assert.Equal(order, followers.Items.Select(f => f.UserId).ToArray());
        Assert.False(viewModel.FollowerSelection.HasAny);
    }

    [Fact]
    public void Deleting_with_nothing_ticked_does_nothing_and_one_ticked_ping_names_the_viewer()
    {
        using var dir = new TempDir();
        var pings = new PingStore(dir.File("p.json"));
        pings.AddRange(new[] { new ChatPing { Key = "a", DisplayName = "Anna", Message = "@s hi", AtUtc = MakeFollower.T0 } });
        var viewModel = new MainViewModel(
            new DonationStore(dir.File("d.json")), new FollowerStore(dir.File("f.json")), new SubscriberStore(dir.File("s.json")),
            new RedemptionStore(dir.File("r.json")), pings, 4);

        viewModel.DeleteSelectedPings();
        Assert.Single(pings.Items);
        Assert.False(viewModel.HasUndo);

        pings.Items[0].Selected = true;
        viewModel.DeleteSelectedPings();

        Assert.Empty(pings.Items);
        Assert.Equal("Удалён пинг: Anna", viewModel.UndoText);
    }

    [Fact]
    public void The_refusal_question_goes_away_when_the_selection_changes()
    {
        using var dir = new TempDir();
        var rewards = new RedemptionStore(dir.File("r.json"));
        rewards.AddRange(new[]
        {
            new Redemption { Key = "a", RewardId = "x", CanManage = true },
            new Redemption { Key = "b", RewardId = "x", CanManage = true },
        });
        var viewModel = new MainViewModel(
            new DonationStore(dir.File("d.json")), new FollowerStore(dir.File("f.json")), new SubscriberStore(dir.File("s.json")),
            rewards, new PingStore(dir.File("p.json")), 3);

        viewModel.BeginRewardsRejectConfirm();
        Assert.False(viewModel.ConfirmingRewardsReject);

        rewards.Items[0].Selected = true;
        viewModel.BeginRewardsRejectConfirm();
        Assert.True(viewModel.ConfirmingRewardsReject);
        Assert.True(viewModel.ShowRewardsBulkConfirm);
        Assert.False(viewModel.ShowRewardsBulkActions);

        rewards.Items[1].Selected = true;

        Assert.False(viewModel.ConfirmingRewardsReject);
        Assert.True(viewModel.ShowRewardsBulkActions);
    }

    [Fact]
    public void Ticked_subscriptions_are_deleted_together_and_one_undo_puts_them_back_in_place()
    {
        using var dir = new TempDir();
        var subscribers = new SubscriberStore(dir.File("s.json"));
        subscribers.AddRange(new[]
        {
            new Subscriber { Key = "s1", DisplayName = "Test_Sub", AtUtc = MakeFollower.T0 },
            new Subscriber { Key = "s2", Kind = SubscriptionKind.Gift, IsAnonymous = true, GiftTotal = 5, AtUtc = MakeFollower.T0.AddMinutes(1) },
            new Subscriber { Key = "s3", Kind = SubscriptionKind.Resub, DisplayName = "Old_Friend", AtUtc = MakeFollower.T0.AddMinutes(2) },
        });
        var order = subscribers.Items.Select(s => s.Key).ToArray();
        var viewModel = new MainViewModel(
            new DonationStore(dir.File("d.json")), new FollowerStore(dir.File("f.json")), subscribers,
            new RedemptionStore(dir.File("r.json")), new PingStore(dir.File("p.json")), 2);

        viewModel.SubscriberSelection.ToggleAll();
        Assert.Equal(3, viewModel.SubscriberSelection.Count);
        Assert.Equal("Снять выбор", viewModel.SubscriberSelection.ToggleAllText);
        viewModel.DeleteSelectedSubscribers();

        Assert.Empty(subscribers.Items);
        Assert.Equal("Удалено подписок: 3", viewModel.UndoText);
        viewModel.Undo();
        Assert.Equal(order, subscribers.Items.Select(s => s.Key).ToArray());
        Assert.False(viewModel.SubscriberSelection.HasAny);

        subscribers.Items.Single(s => s.Key == "s3").Selected = true;
        viewModel.DeleteSelectedSubscribers();
        Assert.Equal("Удалено: Old_Friend (продление)", viewModel.UndoText);
    }

    [Fact]
    public void Ticked_donations_are_deleted_together_and_one_undo_puts_them_back()
    {
        using var dir = new TempDir();
        var donations = new DonationStore(dir.File("d.json"));
        donations.AddRange(new[]
        {
            new Donation { Id = 1, Username = "Anna", Amount = 500, Currency = "RUB", CreatedAtUtc = MakeFollower.T0 },
            new Donation { Id = 2, Username = "Mark", Amount = 100, Currency = "RUB", CreatedAtUtc = MakeFollower.T0.AddMinutes(1) },
            new Donation { Id = 3, Username = "Lisa", Amount = 250, Currency = "RUB", CreatedAtUtc = MakeFollower.T0.AddMinutes(2) },
        });
        var order = donations.Items.Select(d => d.Id).ToArray();
        var viewModel = new MainViewModel(
            donations, new FollowerStore(dir.File("f.json")), new SubscriberStore(dir.File("s.json")),
            new RedemptionStore(dir.File("r.json")), new PingStore(dir.File("p.json")), 0);

        donations.Items.Single(d => d.Id == 2).Selected = true;
        viewModel.DeleteSelectedDonations();
        Assert.Equal("Удалено: Mark · 100 ₽", viewModel.UndoText);
        Assert.Equal(2, donations.Items.Count);

        viewModel.Undo();
        Assert.Equal(order, donations.Items.Select(d => d.Id).ToArray());

        viewModel.DonationSelection.ToggleAll();
        viewModel.DeleteSelectedDonations();
        Assert.Empty(donations.Items);
        Assert.Equal("Удалено донатов: 3", viewModel.UndoText);
        viewModel.Undo();
        Assert.Equal(order, donations.Items.Select(d => d.Id).ToArray());
    }

    [Fact]
    public void A_selection_is_not_written_to_disk()
    {
        using var dir = new TempDir();
        var donations = new DonationStore(dir.File("d.json"));
        donations.AddRange(new[] { new Donation { Id = 7, Username = "X", Amount = 1, Currency = "RUB", CreatedAtUtc = MakeFollower.T0 } });
        donations.Items[0].Selected = true;
        var subscribers = new SubscriberStore(dir.File("s.json"));
        subscribers.AddRange(new[] { new Subscriber { Key = "k", DisplayName = "Y", AtUtc = MakeFollower.T0 } });
        subscribers.Items[0].Selected = true;
        donations.Items[0].Seen = true;
        subscribers.Items[0].Seen = true;

        Assert.False(new DonationStore(dir.File("d.json")).Items[0].Selected);
        Assert.False(new SubscriberStore(dir.File("s.json")).Items[0].Selected);
        Assert.DoesNotContain("Selected", File.ReadAllText(dir.File("d.json")));
        Assert.DoesNotContain("Selected", File.ReadAllText(dir.File("s.json")));
    }
}