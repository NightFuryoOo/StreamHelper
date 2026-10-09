using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class HiddenRewardTests
{
    private static AppSettings WithPair() => new()
    {
        ManagedRewardIds = new List<string> { "copy-a", "copy-b" },
        RewardCopies = new Dictionary<string, string> { ["orig-a"] = "copy-a", ["orig-b"] = "copy-b" },
    };

    [Fact]
    public void Nothing_is_hidden_by_default()
    {
        var settings = WithPair();

        Assert.Empty(settings.HiddenRewardIds);
        Assert.False(settings.IsRewardHidden("copy-a"));
        Assert.True(settings.ListsReward("copy-a"));
        Assert.True(settings.ListsReward("orig-a"));
    }

    [Fact]
    public void An_unticked_reward_is_taken_but_not_listed_and_its_original_keeps_its_own_tick()
    {
        var settings = WithPair();
        settings.HiddenRewardIds.Add("copy-a");

        Assert.True(settings.AllowsReward("copy-a"));
        Assert.False(settings.ListsReward("copy-a"));
        Assert.True(settings.ListsReward("orig-a"));
        Assert.True(settings.ListsReward("copy-b"));
        Assert.True(settings.ListsReward("orig-b"));
        Assert.True(settings.ListsReward("unrelated"));

        settings.OnlyOwnRewards = true;
        Assert.False(settings.ListsReward("copy-a"));
        Assert.True(settings.ListsReward("copy-b"));
        Assert.False(settings.AllowsReward("orig-b"));
    }

    [Fact]
    public void Showing_it_again_brings_its_orders_back()
    {
        var settings = WithPair();
        settings.HiddenRewardIds.Add("copy-a");
        Assert.False(settings.ListsReward("copy-a"));

        settings.HiddenRewardIds.Remove("copy-a");

        Assert.True(settings.ListsReward("copy-a"));
    }

    [Fact]
    public void Unticking_an_original_leaves_its_copy_ticked()
    {
        var settings = WithPair();
        settings.HiddenRewardIds.Add("orig-b");

        Assert.False(settings.IsRewardHidden("copy-b"));
        Assert.True(settings.IsRewardHidden("orig-b"));
        Assert.False(settings.IsRewardHidden("copy-a"));
    }

    [Fact]
    public void A_deleted_reward_is_forgotten_by_the_hidden_list_and_the_others_stay()
    {
        using var dir = new TempDir();
        var settings = WithPair();
        settings.HiddenRewardIds.AddRange(new[] { "copy-a", "copy-b" });
        var store = new RedemptionStore(dir.File("redemptions.json"));

        RewardCleanup.ApplyLocally(settings, store, new[] { "copy-a" });

        Assert.Equal(new[] { "copy-b" }, settings.HiddenRewardIds.ToArray());
    }

    [Fact]
    public void The_hidden_list_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.RewardCopies["orig-a"] = "copy-a";
        store.Current.HiddenRewardIds.Add("copy-a");
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal(new[] { "copy-a" }, reloaded.HiddenRewardIds.ToArray());
        Assert.False(reloaded.ListsReward("copy-a"));
    }

    [Fact]
    public void A_row_of_the_reward_list_tells_its_state_and_notifies_the_change()
    {
        var reward = new RewardRow("copy-a", "Название​", 100, enabled: true, own: true, canManage: true, pendingCount: 0, shown: true);
        var changed = new List<string>();
        reward.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        Assert.Equal("Название · 100 баллов", reward.DisplayText);
        Assert.Equal("Выключить «Название» на канале", reward.SwitchName);
        Assert.Equal("Удалить «Название»", reward.DeleteName);

        reward.Enabled = false;
        reward.Enabled = false;

        Assert.Equal("Название · 100 баллов · выключена", reward.DisplayText);
        Assert.Equal("Включить «Название» на канале", reward.SwitchName);
        Assert.Equal(new[] { nameof(RewardRow.Enabled), nameof(RewardRow.DisplayText), nameof(RewardRow.SwitchName) }, changed.ToArray());

        var twitch = new RewardRow("orig", "Своя", 2, enabled: true, own: false, canManage: false, pendingCount: 0, shown: false);
        Assert.Equal("Своя (Создано Twitch) · 2 балла", twitch.DisplayText);
        Assert.True(twitch.TwitchMade);
        var pending = new RewardRow("p", "Трек", 500, enabled: true, own: true, canManage: true, pendingCount: 2, shown: true);
        Assert.EndsWith("необработанных заказов: 2", pending.DisplayText);
        Assert.Contains("Необработанные заказы (2)", pending.ConfirmText);
    }
    [Fact]
    public void Orders_of_an_unticked_reward_only_sound_and_the_others_are_listed()
    {
        var settings = WithPair();
        settings.HiddenRewardIds.Add("copy-a");
        var items = new[]
        {
            new Redemption { Key = "1", RewardId = "copy-a" },
            new Redemption { Key = "2", RewardId = "orig-a" },
            new Redemption { Key = "3", RewardId = "copy-b" },
            new Redemption { Key = "4", RewardId = "" },
        };

        var (listed, soundOnly) = RedemptionRouting.Split(settings, items);

        Assert.Equal(new[] { "2", "3", "4" }, listed.Select(r => r.Key).ToArray());
        Assert.Equal(new[] { "1" }, soundOnly.Select(r => r.Key).ToArray());
    }
}