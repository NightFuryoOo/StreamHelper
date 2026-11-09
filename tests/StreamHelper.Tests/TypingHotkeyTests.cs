using System.Windows.Input;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class TypingHotkeyTests
{
    [Fact]
    public void The_write_key_is_recognised_while_typing_even_on_the_number_pad()
    {
        var numpad = new HotkeyBinding { Modifiers = 0, VirtualKey = 0x62 };

        Assert.True(ChatRules.IsHotkeyPress(numpad, Key.NumPad2, ModifierKeys.None));
        Assert.False(ChatRules.IsHotkeyPress(numpad, Key.D2, ModifierKeys.None));
        Assert.False(ChatRules.IsHotkeyPress(numpad, Key.NumPad2, ModifierKeys.Control));
    }

    [Fact]
    public void A_combination_needs_exactly_its_modifiers()
    {
        var combo = new HotkeyBinding { Modifiers = (uint)(ModifierKeys.Control | ModifierKeys.Shift), VirtualKey = 0x54 };

        Assert.True(ChatRules.IsHotkeyPress(combo, Key.T, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(ChatRules.IsHotkeyPress(combo, Key.T, ModifierKeys.Control));
        Assert.False(ChatRules.IsHotkeyPress(combo, Key.T, ModifierKeys.None));
    }

    [Fact]
    public void The_mouse_key_works_while_typing_even_without_a_write_key()
    {
        var settings = new AppSettings();
        settings.SetHotkey(HotkeyAction.ChatInteract, 0, 0x63);
        settings.SetHotkey(HotkeyAction.ChatToggle, 0, 0x62);
        settings.SetHotkey(HotkeyAction.Donations, 0, 0x67);

        Assert.Equal(HotkeyAction.ChatInteract, ChatRules.KeyWhileTyping(settings, Key.NumPad3, ModifierKeys.None));
        Assert.Null(ChatRules.KeyWhileTyping(settings, Key.D3, ModifierKeys.None));
        Assert.Null(ChatRules.KeyWhileTyping(settings, Key.NumPad2, ModifierKeys.None));
        Assert.Null(ChatRules.KeyWhileTyping(settings, Key.NumPad7, ModifierKeys.None));
    }

    [Fact]
    public void The_write_key_wins_when_both_keys_are_the_same()
    {
        var settings = new AppSettings();
        settings.SetHotkey(HotkeyAction.ChatInteract, 0, 0x63);
        settings.SetHotkey(HotkeyAction.ChatWrite, 0, 0x63);

        Assert.Equal(HotkeyAction.ChatWrite, ChatRules.KeyWhileTyping(settings, Key.NumPad3, ModifierKeys.None));
    }

    [Fact]
    public void Without_a_write_key_nothing_is_taken_away_from_typing()
    {
        Assert.False(ChatRules.IsHotkeyPress(new HotkeyBinding(), Key.None, ModifierKeys.None));
        Assert.False(ChatRules.IsHotkeyPress(new AppSettings().GetHotkey(HotkeyAction.ChatWrite), Key.Enter, ModifierKeys.None));
    }
}
