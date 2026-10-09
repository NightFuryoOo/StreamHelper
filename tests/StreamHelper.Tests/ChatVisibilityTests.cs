using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class ChatVisibilityTests
{
    [Fact]
    public void The_chat_is_shown_by_default()
    {
        Assert.False(new AppSettings().ChatHidden);
    }

    [Fact]
    public void An_old_settings_file_that_had_the_chat_switched_off_still_gets_the_chat_shown()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("old.json"), "{\"ChatOverlayEnabled\":false,\"NotifyRewards\":false}");

        var settings = new SettingsStore(dir.File("old.json")).Current;

        Assert.False(settings.ChatHidden);
        Assert.False(settings.NotifyRewards);
    }

    [Fact]
    public void Hiding_the_chat_with_the_key_is_remembered()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ChatHidden = true;
        store.Save();

        Assert.True(new SettingsStore(dir.File("settings.json")).Current.ChatHidden);
    }
}