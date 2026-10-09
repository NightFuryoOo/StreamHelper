using System.Runtime.InteropServices;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData(AppSettings.ModControl | AppSettings.ModAlt, 0x4Fu, "Ctrl + Alt + O")]
    [InlineData(AppSettings.ModControl | AppSettings.ModShift | AppSettings.ModAlt | AppSettings.ModWin, 0x70u, "Ctrl + Alt + Shift + Win + F1")]
    [InlineData(0u, 0x77u, "F8")]
    [InlineData(0u, 0x0Du, "Enter")]
    [InlineData(0u, 0x20u, "Space")]
    [InlineData(0u, 0xC0u, "` (тильда)")]
    [InlineData(0u, 0x31u, "1")]
    [InlineData(0u, 0x61u, "Num 1")]
    [InlineData(0u, 0x2Du, "Insert")]
    [InlineData(0u, 0x22u, "PageDown")]
    [InlineData(0u, 0x91u, "ScrollLock")]
    [InlineData(AppSettings.ModShift, 0x41u, "Shift + A")]
    public void Describes_the_binding(uint modifiers, uint virtualKey, string expected) =>
        Assert.Equal(expected, HotkeyService.Describe(modifiers, virtualKey));

    [Fact]
    public void An_unset_binding_is_shown_as_not_set() =>
        Assert.Equal("Не задано", HotkeyService.Describe(0, 0));

}

public class HotkeyBindingTests
{
    [Fact]
    public void Every_action_starts_unset_and_shows_not_set()
    {
        var settings = new AppSettings();

        foreach (var action in HotkeyActions.All)
        {
            Assert.False(settings.GetHotkey(action).IsSet, action.ToString());
            Assert.Equal("Не задано", HotkeyService.Describe(settings.GetHotkey(action)));
        }
        Assert.Empty(settings.Hotkeys);
    }

    [Fact]
    public void Resetting_a_binding_makes_it_not_set_again()
    {
        var settings = new AppSettings();
        settings.SetHotkey(HotkeyAction.Rewards, AppSettings.ModControl, 0x71);
        Assert.Equal("Ctrl + F2", HotkeyService.Describe(settings.GetHotkey(HotkeyAction.Rewards)));

        settings.SetHotkey(HotkeyAction.Rewards, 0, 0);

        Assert.Equal("Не задано", HotkeyService.Describe(settings.GetHotkey(HotkeyAction.Rewards)));
        Assert.Empty(settings.Hotkeys);
    }
    [Fact]
    public void Every_action_keeps_its_own_binding_and_clearing_one_leaves_the_rest()
    {
        var settings = new AppSettings();
        var key = 0x70u;
        foreach (var action in HotkeyActions.All) settings.SetHotkey(action, AppSettings.ModControl, key++);

        key = 0x70u;
        foreach (var action in HotkeyActions.All)
        {
            var binding = settings.GetHotkey(action);
            Assert.Equal((AppSettings.ModControl, key++), (binding.Modifiers, binding.VirtualKey));
        }

        settings.SetHotkey(HotkeyAction.Followers, 0, 0);

        Assert.False(settings.GetHotkey(HotkeyAction.Followers).IsSet);
        Assert.True(settings.GetHotkey(HotkeyAction.Donations).IsSet);
        Assert.True(settings.GetHotkey(HotkeyAction.Subscribers).IsSet);
        Assert.DoesNotContain("Followers", settings.Hotkeys.Keys);
    }

    [Fact]
    public void A_combination_already_used_by_another_action_is_found_but_not_by_the_action_itself()
    {
        var settings = new AppSettings();
        settings.SetHotkey(HotkeyAction.Rewards, AppSettings.ModControl | AppSettings.ModAlt, 0x71);

        Assert.Equal(HotkeyAction.Rewards, settings.FindHotkeyOwner(AppSettings.ModControl | AppSettings.ModAlt, 0x71, HotkeyAction.Donations));
        Assert.Null(settings.FindHotkeyOwner(AppSettings.ModControl | AppSettings.ModAlt, 0x71, HotkeyAction.Rewards));
        Assert.Null(settings.FindHotkeyOwner(AppSettings.ModControl, 0x71, HotkeyAction.Donations));
        Assert.Null(settings.FindHotkeyOwner(AppSettings.ModControl | AppSettings.ModAlt, 0x72, HotkeyAction.Donations));
    }

    [Fact]
    public void The_tab_actions_map_to_the_window_tabs_in_order_and_the_others_to_none()
    {
        Assert.Equal(0, HotkeyActions.TabIndex(HotkeyAction.Donations));
        Assert.Equal(1, HotkeyActions.TabIndex(HotkeyAction.Followers));
        Assert.Equal(2, HotkeyActions.TabIndex(HotkeyAction.Subscribers));
        Assert.Equal(3, HotkeyActions.TabIndex(HotkeyAction.Rewards));
        Assert.Equal(4, HotkeyActions.TabIndex(HotkeyAction.Pings));
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.Settings));
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.HideToasts));
        Assert.All(HotkeyActions.All, a => Assert.False(string.IsNullOrWhiteSpace(HotkeyActions.Title(a))));
    }

    [Fact]
    public void The_bindings_survive_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.SetHotkey(HotkeyAction.Donations, AppSettings.ModControl, 0x71);
        store.Current.SetHotkey(HotkeyAction.Settings, AppSettings.ModAlt | AppSettings.ModShift, 0x53);
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal("Ctrl + F2", HotkeyService.Describe(reloaded.GetHotkey(HotkeyAction.Donations)));
        Assert.Equal("Alt + Shift + S", HotkeyService.Describe(reloaded.GetHotkey(HotkeyAction.Settings)));
        Assert.False(reloaded.GetHotkey(HotkeyAction.Rewards).IsSet);
    }

    [Fact]
    public void Settings_saved_with_the_old_show_hide_key_still_load_and_nothing_is_bound()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"HotkeyModifiers\":3,\"HotkeyVirtualKey\":79,\"ShowToast\":false}");

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.False(loaded.ShowToast);
        Assert.All(HotkeyActions.All, action => Assert.False(loaded.GetHotkey(action).IsSet, action.ToString()));
    }
    [Fact]
    public void A_null_bindings_entry_in_the_file_does_not_break_anything()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"Hotkeys\":null}");

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.False(loaded.GetHotkey(HotkeyAction.Donations).IsSet);
        loaded.SetHotkey(HotkeyAction.Donations, 0, 0x71);
        Assert.True(loaded.GetHotkey(HotkeyAction.Donations).IsSet);
    }
}

public class HotkeyServiceTests
{
    private const uint All4 = AppSettings.ModControl | AppSettings.ModAlt | AppSettings.ModShift | AppSettings.ModWin;
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void Press(HotkeyService service, HotkeyAction action) =>
        SendMessage(service.Handle, WmHotkey, (IntPtr)HotkeyService.IdOf(action), IntPtr.Zero);

    private static void OnUiThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void Each_action_reports_itself_when_its_key_is_pressed()
    {
        OnUiThread(() =>
        {
            using var service = new HotkeyService();
            var pressed = new List<HotkeyAction>();
            service.Pressed += pressed.Add;
            Assert.True(service.Register(HotkeyAction.Donations, All4, 0x7C));
            Assert.True(service.Register(HotkeyAction.Settings, All4, 0x7D));

            Press(service, HotkeyAction.Settings);
            Press(service, HotkeyAction.Donations);
            Press(service, HotkeyAction.Settings);

            Assert.Equal(new[] { HotkeyAction.Settings, HotkeyAction.Donations, HotkeyAction.Settings }, pressed);
        });
    }

    [Fact]
    public void A_combination_cannot_be_registered_twice_until_the_first_is_released()
    {
        OnUiThread(() =>
        {
            using var service = new HotkeyService();
            Assert.True(service.Register(HotkeyAction.Donations, All4, 0x7E));

            Assert.False(service.Register(HotkeyAction.Followers, All4, 0x7E));
            Assert.False(service.IsRegistered(HotkeyAction.Followers));
            Assert.True(service.IsRegistered(HotkeyAction.Donations));

            service.Unregister(HotkeyAction.Donations);
            Assert.True(service.Register(HotkeyAction.Followers, All4, 0x7E));
        });
    }

    [Fact]
    public void Registering_all_skips_the_unset_ones_and_names_the_ones_that_failed()
    {
        OnUiThread(() =>
        {
            using var service = new HotkeyService();
            var settings = new AppSettings();
            settings.SetHotkey(HotkeyAction.Followers, All4, 0x7F);
            settings.SetHotkey(HotkeyAction.Rewards, All4, 0x80);
            settings.SetHotkey(HotkeyAction.Settings, All4, 0x80);

            var failed = service.RegisterAll(settings);

            Assert.Equal(new[] { HotkeyAction.Settings }, failed);
            Assert.True(service.IsRegistered(HotkeyAction.Followers));
            Assert.True(service.IsRegistered(HotkeyAction.Rewards));
            Assert.False(service.IsRegistered(HotkeyAction.Donations));
            Assert.False(service.IsRegistered(HotkeyAction.Settings));
        });
    }

    [Fact]
    public void Clearing_a_binding_and_registering_again_releases_it()
    {
        OnUiThread(() =>
        {
            using var service = new HotkeyService();
            var settings = new AppSettings();
            settings.SetHotkey(HotkeyAction.Followers, All4, 0x81);
            Assert.Empty(service.RegisterAll(settings));
            Assert.True(service.IsRegistered(HotkeyAction.Followers));

            settings.SetHotkey(HotkeyAction.Followers, 0, 0);
            service.RegisterAll(settings);

            Assert.False(service.IsRegistered(HotkeyAction.Followers));
        });
    }

    [Fact]
    public void Disposing_releases_every_key()
    {
        OnUiThread(() =>
        {
            var first = new HotkeyService();
            Assert.True(first.Register(HotkeyAction.Donations, All4, 0x82));
            Assert.True(first.Register(HotkeyAction.Rewards, All4, 0x83));
            first.Dispose();

            using var second = new HotkeyService();
            Assert.True(second.Register(HotkeyAction.Donations, All4, 0x82));
            Assert.True(second.Register(HotkeyAction.Rewards, All4, 0x83));
        });
    }
}