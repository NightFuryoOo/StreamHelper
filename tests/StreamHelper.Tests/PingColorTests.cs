using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class PingColorTests
{
    private static ChatMessage Message(bool ping) =>
        new("m", "42", "alice", "Alice", "@streamer hi", Array.Empty<ChatMention>(), MakeFollower.T0, IsPing: ping);

    [Fact]
    public void Pings_are_marked_in_orange_by_default()
    {
        var settings = new AppSettings();

        Assert.True(settings.ChatPingHighlight);
        Assert.Equal("#FF9F0A", ChatHighlights.PingColor(settings));
        Assert.Equal(ChatHighlights.Palette.Single(s => s.Name == "Оранжевый").Hex, ChatHighlights.PingColor(settings));
        Assert.True(ChatHighlights.MarksPing(settings, Message(true)));
        Assert.False(ChatHighlights.MarksPing(settings, Message(false)));
    }

    [Fact]
    public void The_switch_turns_the_mark_off_and_a_broken_color_falls_back_to_orange()
    {
        var settings = new AppSettings { ChatPingHighlight = false, ChatPingColor = "#123" };

        Assert.False(ChatHighlights.MarksPing(settings, Message(true)));
        Assert.Equal("#112233", ChatHighlights.PingColor(settings));
        settings.ChatPingColor = "not a color";
        Assert.Equal("#FF9F0A", ChatHighlights.PingColor(settings));
        settings.ChatPingColor = null!;
        Assert.Equal("#FF9F0A", ChatHighlights.PingColor(settings));
    }

    [Fact]
    public void The_chosen_color_and_switch_survive_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ChatPingColor = "#30D158";
        store.Current.ChatPingHighlight = false;
        store.Save();

        var loaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal("#30D158", ChatHighlights.PingColor(loaded));
        Assert.False(loaded.ChatPingHighlight);
        Assert.Contains(nameof(AppSettings.ChatPingColor), Profile.SharedSettings);
        Assert.Contains(nameof(AppSettings.ChatPingHighlight), Profile.SharedSettings);
    }

    [Fact]
    public void The_ping_outline_is_a_soft_line_in_the_ping_colour()
    {
        Assert.InRange(ChatHighlights.PingLineOpacity, 0.3, 0.7);
    }
}
