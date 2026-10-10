using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class HiddenTabTests
{
    [Fact]
    public void Tabs_can_be_hidden_and_shown_again_but_the_last_one_always_stays()
    {
        var settings = new AppSettings();
        for (var i = 0; i < 5; i++) settings.SetTabHidden(i, true);

        Assert.Equal(1, settings.VisibleTabCount());
        settings.SetTabHidden(5, true);
        Assert.False(settings.IsTabHidden(5));
        Assert.Equal(new[] { "Donations", "Followers", "Subscribers", "Rewards", "Pings" }, settings.HiddenTabs);

        settings.SetTabHidden(2, false);
        settings.SetTabHidden(2, false);
        Assert.False(settings.IsTabHidden(2));
        Assert.Equal(4, settings.HiddenTabs.Count);
        settings.SetTabHidden(9, true);
        Assert.Equal(4, settings.HiddenTabs.Count);
    }

    [Fact]
    public void A_hidden_selected_tab_falls_back_to_the_first_visible_one()
    {
        var settings = new AppSettings();
        settings.SetTabHidden(0, true);
        settings.SetTabHidden(3, true);

        Assert.Equal(1, settings.VisibleTab(3));
        Assert.Equal(4, settings.VisibleTab(4));
        Assert.Equal(1, settings.VisibleTab(0));
        Assert.Equal(1, settings.VisibleTab(42));
    }

    [Fact]
    public void A_file_that_hides_every_tab_hides_none()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"),
            "{\"HiddenTabs\":[\"Donations\",\"Followers\",\"Subscribers\",\"Rewards\",\"Pings\",\"Moments\",\"Unknown\"]}");

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.All(Enumerable.Range(0, 6), i => Assert.False(loaded.IsTabHidden(i)));
        Assert.Equal(0, loaded.VisibleTab(0));
    }

    [Fact]
    public void Hidden_tabs_survive_a_restart_and_null_is_harmless()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.SetTabHidden(4, true);
        store.Save();
        Assert.True(new SettingsStore(dir.File("settings.json")).Current.IsTabHidden(4));

        File.WriteAllText(dir.File("settings.json"), "{\"HiddenTabs\":null}");
        var empty = new SettingsStore(dir.File("settings.json")).Current;
        Assert.False(empty.IsTabHidden(4));
        empty.SetTabHidden(4, true);
        Assert.True(empty.IsTabHidden(4));
    }

    [Fact]
    public void The_window_hides_the_tab_moves_off_it_and_stops_counting_its_unseen_items()
    {
        using var dir = new TempDir();
        var pings = new PingStore(dir.File("p.json"));
        pings.AddRange(new[] { new ChatPing { Key = "a", DisplayName = "Anna", Message = "@s hi", AtUtc = MakeFollower.T0 } });
        var donations = new DonationStore(dir.File("d.json"));
        var viewModel = new MainViewModel(
            donations, new FollowerStore(dir.File("f.json")), new SubscriberStore(dir.File("s.json")),
            new RedemptionStore(dir.File("r.json")), pings, new MomentStore(dir.File("m.json")), 4);
        var settings = new AppSettings();
        Assert.Equal(1, viewModel.TotalUnseen);

        settings.SetTabHidden(4, true);
        settings.SetTabHidden(0, true);
        viewModel.ApplyHiddenTabs(settings);

        Assert.False(viewModel.ShowPingsTab);
        Assert.False(viewModel.ShowDonationsTab);
        Assert.True(viewModel.ShowFollowersTab);
        Assert.True(viewModel.IsHidden(4));
        Assert.Equal(1, viewModel.SelectedTab);
        Assert.Equal(0, viewModel.TotalUnseen);

        settings.SetTabHidden(4, false);
        viewModel.ApplyHiddenTabs(settings);

        Assert.True(viewModel.ShowPingsTab);
        Assert.Equal(1, viewModel.TotalUnseen);
        Assert.Equal(1, viewModel.SelectedTab);
    }

    [Fact]
    public void The_tab_keys_match_the_hotkey_tab_order()
    {
        Assert.Equal(6, MainTabs.Keys.Length);
        Assert.Equal(MainTabs.Keys.Length, MainTabs.Titles.Length);
        foreach (var action in HotkeyActions.All)
        {
            var tab = HotkeyActions.TabIndex(action);
            if (tab >= 0) Assert.Equal(action.ToString(), MainTabs.Keys[tab]);
        }
    }
}
