using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class MuteBadgeTests
{
    private static readonly ScreenBounds Work = new(0, 0, 1920, 1040);
    private static readonly ScreenBounds Virtual = new(0, 0, 1920, 1080);

    private static (RewardAlertService Service, SettingsStore Settings, string Folder) Make(TempDir dir)
    {
        var settings = new SettingsStore(dir.File("settings.json"));
        var folder = dir.File("alerts");
        return (new RewardAlertService(settings, folder), settings, folder);
    }

    [Fact]
    public void The_mute_key_is_an_action_of_its_own_with_a_title_and_no_default_key()
    {
        Assert.Contains(HotkeyAction.RewardSoundsMute, HotkeyActions.All);
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.RewardSoundsMute));
        Assert.Equal("Звуки наград: выкл / вкл", HotkeyActions.Title(HotkeyAction.RewardSoundsMute));
        Assert.False(new AppSettings().GetHotkey(HotkeyAction.RewardSoundsMute).IsSet);
    }

    [Fact]
    public void Switching_the_sounds_off_is_remembered_across_a_restart_and_tells_the_listeners_once()
    {
        using var dir = new TempDir();
        var (service, _, _) = Make(dir);
        var changes = new List<bool>();
        service.MutedChanged += changes.Add;
        Assert.False(service.Muted);

        service.SetMuted(true);
        service.SetMuted(true);
        Assert.True(service.Muted);
        Assert.Equal(new[] { true }, changes);

        var reloaded = new RewardAlertService(new SettingsStore(dir.File("settings.json")), dir.File("alerts"));
        Assert.True(reloaded.Muted);

        Assert.False(service.ToggleMuted());
        Assert.Equal(new[] { true, false }, changes);
        Assert.False(new RewardAlertService(new SettingsStore(dir.File("settings.json")), dir.File("alerts")).Muted);
    }

    [Fact]
    public void While_the_sounds_are_off_no_order_plays_and_the_test_button_path_is_not_affected()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);
        const string reward = "11111111-aaaa-4bbb-8ccc-000000000001";
        var source = dir.File("horn.mp3");
        File.WriteAllBytes(source, Enumerable.Repeat((byte)7, 2048).ToArray());
        Assert.True(service.Choose(reward, source).Success);
        Assert.NotNull(service.PathOf(reward));

        service.SetMuted(true);

        Assert.False(service.PlayOrdered(reward));
        Assert.True(settings.Current.RewardSounds.ContainsKey(reward));
        Assert.NotNull(service.PathOf(reward));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_reward_without_a_sound_has_nothing_to_play_even_when_the_sounds_are_on()
    {
        using var dir = new TempDir();
        var (service, _, _) = Make(dir);

        Assert.False(service.PlayOrdered("22222222-aaaa-4bbb-8ccc-000000000002"));
    }

    [Fact]
    public void The_icon_starts_in_the_top_right_corner_of_the_work_area_with_a_small_margin()
    {
        var size = MuteBadgePlacement.BaseSize;

        var (left, top) = MuteBadgePlacement.Resolve(null, null, size, Work, Virtual);

        Assert.Equal(1920 - size - MuteBadgePlacement.Margin, left);
        Assert.Equal(MuteBadgePlacement.Margin, top);
    }

    [Fact]
    public void A_saved_place_is_kept_even_at_the_very_edge_and_one_off_every_screen_goes_back_to_the_corner()
    {
        var size = 24.0;

        Assert.Equal((0.0, 0.0), MuteBadgePlacement.Resolve(0, 0, size, Work, Virtual));
        Assert.Equal((300.0, 200.0), MuteBadgePlacement.Resolve(300, 200, size, Work, Virtual));
        var corner = MuteBadgePlacement.Resolve(null, null, size, Work, Virtual);
        Assert.Equal(corner, MuteBadgePlacement.Resolve(5000, 10, size, Work, Virtual));
        Assert.Equal(corner, MuteBadgePlacement.Resolve(-900, 10, size, Work, Virtual));
        Assert.Equal(corner, MuteBadgePlacement.Resolve(double.NaN, 10, size, Work, Virtual));
    }

    [Fact]
    public void The_icon_never_sticks_out_of_the_screens()
    {
        var size = 160.0;

        var (left, top) = MuteBadgePlacement.Resolve(1900, 1070, size, Work, Virtual);

        Assert.Equal(1920 - size, left);
        Assert.Equal(1080 - size, top);
    }

    [Fact]
    public void The_size_is_kept_between_its_limits_and_a_broken_value_means_the_base_size()
    {
        Assert.Equal(1.0, MuteBadgePlacement.ClampScale(double.NaN));
        Assert.Equal(MuteBadgePlacement.MinScale, MuteBadgePlacement.ClampScale(0.01));
        Assert.Equal(MuteBadgePlacement.MaxScale, MuteBadgePlacement.ClampScale(99));
        Assert.Equal(1.5, MuteBadgePlacement.ClampScale(1.5));
    }

    [Fact]
    public void The_icon_settings_survive_a_restart_and_default_to_the_base_size_at_the_corner()
    {
        using var dir = new TempDir();
        var fresh = new AppSettings();
        Assert.False(fresh.RewardSoundsMuted);
        Assert.Equal(1.0, fresh.MuteBadgeScale);
        Assert.Null(fresh.MuteBadgeLeft);
        Assert.Null(fresh.MuteBadgeTop);

        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.RewardSoundsMuted = true;
        store.Current.MuteBadgeScale = 2.25;
        store.Current.MuteBadgeLeft = 1800;
        store.Current.MuteBadgeTop = 12;
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;
        Assert.True(reloaded.RewardSoundsMuted);
        Assert.Equal(2.25, reloaded.MuteBadgeScale);
        Assert.Equal(1800, reloaded.MuteBadgeLeft);
        Assert.Equal(12, reloaded.MuteBadgeTop);
    }

    [Fact]
    public void The_icon_is_shown_while_the_sounds_are_off_unless_it_is_switched_off_and_always_while_it_is_placed()
    {
        Assert.False(MuteBadgeService.ShouldShow(muted: false, iconHidden: false, positioning: false));
        Assert.True(MuteBadgeService.ShouldShow(muted: true, iconHidden: false, positioning: false));
        Assert.False(MuteBadgeService.ShouldShow(muted: true, iconHidden: true, positioning: false));
        Assert.True(MuteBadgeService.ShouldShow(muted: true, iconHidden: true, positioning: true));
        Assert.True(MuteBadgeService.ShouldShow(muted: false, iconHidden: false, positioning: true));
    }

    [Fact]
    public void Switching_the_icon_off_is_remembered_and_off_by_default()
    {
        using var dir = new TempDir();
        Assert.False(new AppSettings().MuteBadgeHidden);
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.MuteBadgeHidden = true;
        store.Save();

        Assert.True(new SettingsStore(dir.File("settings.json")).Current.MuteBadgeHidden);
    }

    [Fact]
    public void Folding_the_developer_line_is_remembered_and_it_is_open_by_default()
    {
        using var dir = new TempDir();
        Assert.False(new AppSettings().CreditsCollapsed);
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.CreditsCollapsed = true;
        store.Save();

        Assert.True(new SettingsStore(dir.File("settings.json")).Current.CreditsCollapsed);
    }
}