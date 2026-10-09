using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatCellTests
{
    [Fact]
    public void The_plate_behind_a_message_is_light_but_faint_by_default()
    {
        Assert.Equal(0.2, new AppSettings().ChatCellOpacity);
        Assert.Equal(0.2, ChatPlacement.DefaultCellOpacity);
    }

    [Fact]
    public void The_plate_can_go_all_the_way_down_to_nothing_and_up_to_solid()
    {
        Assert.Equal(0.0, ChatPlacement.ClampCellOpacity(0));
        Assert.Equal(0.0, ChatPlacement.ClampCellOpacity(-0.4));
        Assert.Equal(1.0, ChatPlacement.ClampCellOpacity(3));
        Assert.Equal(0.35, ChatPlacement.ClampCellOpacity(0.35));
        Assert.Equal(0.2, ChatPlacement.ClampCellOpacity(double.NaN));
    }

    [Fact]
    public void The_background_can_be_taken_away_completely()
    {
        Assert.Equal(0.0, ChatPlacement.MinOpacity);
        Assert.Equal(0.0, ChatPlacement.ClampOpacity(0));
        Assert.Equal(0.0, ChatPlacement.ClampOpacity(-1));
        Assert.Equal(0.6, ChatPlacement.ClampOpacity(double.NaN));
        Assert.Equal(0.05, ChatPlacement.ClampOpacity(0.05));
    }

    [Fact]
    public void The_plate_choice_survives_a_restart_and_an_old_outline_setting_is_ignored()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ChatCellOpacity = 0.35;
        store.Save();
        Assert.Equal(0.35, new SettingsStore(dir.File("settings.json")).Current.ChatCellOpacity);

        File.WriteAllText(dir.File("old.json"), "{\"ChatBorderOpacity\":0.9}");
        Assert.Equal(0.2, new SettingsStore(dir.File("old.json")).Current.ChatCellOpacity);
    }

    [Fact]
    public void The_black_outline_is_nearly_solid_by_default_and_can_go_to_nothing_and_up_to_solid()
    {
        Assert.Equal(0.9, new AppSettings().ChatOutlineOpacity);
        Assert.Equal(0.9, ChatPlacement.DefaultOutlineOpacity);
        Assert.Equal(0.0, ChatPlacement.ClampOutlineOpacity(0));
        Assert.Equal(0.0, ChatPlacement.ClampOutlineOpacity(-1));
        Assert.Equal(1.0, ChatPlacement.ClampOutlineOpacity(5));
        Assert.Equal(0.4, ChatPlacement.ClampOutlineOpacity(0.4));
        Assert.Equal(0.9, ChatPlacement.ClampOutlineOpacity(double.NaN));
    }

    [Fact]
    public void The_outline_and_the_plate_are_saved_independently()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ChatOutlineOpacity = 0.3;
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal(0.3, reloaded.ChatOutlineOpacity);
        Assert.Equal(0.2, reloaded.ChatCellOpacity);
    }
}