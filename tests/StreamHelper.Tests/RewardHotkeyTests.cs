using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RewardHotkeyTests
{
    private const string RewardA = "11111111-aaaa-4bbb-8ccc-000000000001";
    private const string RewardB = "22222222-aaaa-4bbb-8ccc-000000000002";

    private static string Source(TempDir dir, string name)
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, 2048).ToArray());
        return path;
    }

    [Fact]
    public void A_reward_key_is_stored_with_the_other_keys_and_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.RewardSounds[RewardA] = new RewardSound { File = "r-a.mp3", Name = "a.mp3" };
        store.Current.SetHotkey(AppSettings.RewardHotkeyKey(RewardA), AppSettings.ModControl, 0x31);
        store.Save();

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Contains("RewardSound:" + RewardA, loaded.Hotkeys.Keys);
        Assert.Equal("Ctrl + 1", HotkeyService.Describe(loaded.GetHotkey(AppSettings.RewardHotkeyKey(RewardA))));
        var only = Assert.Single(loaded.RewardHotkeys());
        Assert.Equal(RewardA, only.SoundKey);
        Assert.All(HotkeyActions.All, action => Assert.False(loaded.GetHotkey(action).IsSet, action.ToString()));
    }

    [Fact]
    public void Only_rewards_that_still_have_a_sound_get_their_key()
    {
        var settings = new AppSettings();
        settings.RewardSounds[RewardA] = new RewardSound { File = "r-a.mp3" };
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardA), 0, 0x7F);
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardB), 0, 0x80);
        settings.Hotkeys["RewardSound:broken"] = null!;

        Assert.Equal(new[] { RewardA }, settings.RewardHotkeys().Select(pair => pair.SoundKey).ToArray());
        Assert.Null(settings.FindHotkeyOwner(0, 0x80, "Donations"));
    }

    [Fact]
    public void A_reward_key_and_a_window_key_see_each_other_as_taken()
    {
        var settings = new AppSettings();
        settings.RewardSounds[RewardA] = new RewardSound { File = "r-a.mp3" };
        settings.RewardSounds[RewardB] = new RewardSound { File = "r-b.mp3" };
        settings.SetHotkey(HotkeyAction.Rewards, 0, 0x7C);
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardA), AppSettings.ModShift, 0x7D);

        Assert.Equal("Rewards", settings.FindHotkeyOwner(0, 0x7C, AppSettings.RewardHotkeyKey(RewardA)));
        Assert.Equal(AppSettings.RewardHotkeyKey(RewardA), settings.FindHotkeyOwner(AppSettings.ModShift, 0x7D, "Donations"));
        Assert.Equal(AppSettings.RewardHotkeyKey(RewardA), settings.FindHotkeyOwner(AppSettings.ModShift, 0x7D, AppSettings.RewardHotkeyKey(RewardB)));
        Assert.Null(settings.FindHotkeyOwner(AppSettings.ModShift, 0x7D, AppSettings.RewardHotkeyKey(RewardA)));
        Assert.Null(settings.FindHotkeyOwner(0, 0x7D, "Donations"));
    }

    [Fact]
    public void Settings_without_any_keys_or_sounds_have_no_reward_keys()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"Hotkeys\":null,\"RewardSounds\":null}");

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Empty(loaded.RewardHotkeys());
        Assert.Null(loaded.FindHotkeyOwner(0, 0x7C, "Donations"));
    }

    [Fact]
    public void Removing_a_sound_removes_its_key_but_forgetting_a_copy_keeps_the_original_key()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var service = new RewardAlertService(store, dir.File("alerts"));
        store.Current.RewardCopies[RewardA] = RewardB;
        service.Choose(RewardA, Source(dir, "horn.mp3"));
        store.Current.SetHotkey(AppSettings.RewardHotkeyKey(RewardA), 0, 0x7F);
        store.Save();

        service.Forget(new[] { RewardB });
        Assert.True(store.Current.GetHotkey(AppSettings.RewardHotkeyKey(RewardA)).IsSet);

        service.Clear(RewardB);

        Assert.False(store.Current.GetHotkey(AppSettings.RewardHotkeyKey(RewardA)).IsSet);
        var reloaded = new SettingsStore(dir.File("settings.json")).Current;
        Assert.DoesNotContain(AppSettings.RewardHotkeyKey(RewardA), reloaded.Hotkeys.Keys);
    }

    [Fact]
    public void A_copy_whose_original_is_gone_uses_the_original_as_its_sound_key()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var service = new RewardAlertService(store, dir.File("alerts"));
        store.Current.RewardCopies[RewardA] = RewardB;
        service.Choose(RewardB, Source(dir, "horn.mp3"));

        Assert.Equal(RewardA, service.KeyOf(RewardB));
        Assert.Equal(RewardA, service.KeyOf(RewardA));
        Assert.Equal("other", service.KeyOf("other"));
        Assert.Equal(new[] { RewardA }, store.Current.RewardSounds.Keys.ToArray());
    }

    [Fact]
    public void A_key_saved_under_a_copy_moves_to_the_original_that_holds_the_sound()
    {
        var settings = new AppSettings();
        settings.RewardCopies[RewardA] = RewardB;
        settings.RewardSounds[RewardA] = new RewardSound { File = "r-a.mp3" };
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardB), 0, 0xBF);
        Assert.Empty(settings.RewardHotkeys());

        Assert.True(settings.MoveCopyRewardHotkeys());

        Assert.DoesNotContain(AppSettings.RewardHotkeyKey(RewardB), settings.Hotkeys.Keys);
        var moved = Assert.Single(settings.RewardHotkeys());
        Assert.Equal((RewardA, 0xBFu), (moved.SoundKey, moved.Binding.VirtualKey));
        Assert.False(settings.MoveCopyRewardHotkeys());
    }

    [Fact]
    public void Moving_a_copy_key_never_replaces_a_key_the_original_already_has()
    {
        var settings = new AppSettings();
        settings.RewardCopies[RewardA] = RewardB;
        settings.RewardSounds[RewardA] = new RewardSound { File = "r-a.mp3" };
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardA), 0, 0xBE);
        settings.SetHotkey(AppSettings.RewardHotkeyKey(RewardB), 0, 0xBF);
        settings.SetHotkey(AppSettings.RewardHotkeyKey("unknown"), 0, 0xBC);

        Assert.True(settings.MoveCopyRewardHotkeys());

        Assert.Equal(0xBEu, settings.GetHotkey(AppSettings.RewardHotkeyKey(RewardA)).VirtualKey);
        Assert.DoesNotContain(AppSettings.RewardHotkeyKey(RewardB), settings.Hotkeys.Keys);
        Assert.Contains(AppSettings.RewardHotkeyKey("unknown"), settings.Hotkeys.Keys);
    }
}
