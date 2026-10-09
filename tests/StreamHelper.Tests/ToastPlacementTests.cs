using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ToastPlacementTests
{
    private static readonly ScreenBounds Work = new(0, 0, 1920, 1040);
    private static readonly ScreenBounds Virtual = new(0, 0, 1920, 1080);

    [Fact]
    public void Without_a_saved_position_the_toast_sits_in_the_top_right_corner_with_a_margin()
    {
        var (left, top) = ToastPlacement.Resolve(null, null, 340, 90, Work, Virtual);

        Assert.Equal(1920 - 340 - 16, left);
        Assert.Equal(16, top);
    }

    [Fact]
    public void A_bigger_toast_grows_to_the_left_from_the_corner()
    {
        var small = ToastPlacement.Resolve(null, null, 340, 90, Work, Virtual);
        var big = ToastPlacement.Resolve(null, null, 680, 180, Work, Virtual);

        Assert.True(big.Left < small.Left);
        Assert.Equal(1920 - 680 - 16, big.Left);
    }

    [Fact]
    public void A_saved_position_is_used_as_is()
    {
        var (left, top) = ToastPlacement.Resolve(100, 200, 340, 90, Work, Virtual);

        Assert.Equal(100, left);
        Assert.Equal(200, top);
    }

    [Fact]
    public void A_saved_position_on_a_monitor_that_is_gone_falls_back_to_the_corner()
    {
        var (left, top) = ToastPlacement.Resolve(4000, 300, 340, 90, Work, Virtual);

        Assert.Equal(1920 - 340 - 16, left);
        Assert.Equal(16, top);

        var below = ToastPlacement.Resolve(100, 5000, 340, 90, Work, Virtual);
        Assert.Equal(16, below.Top);
    }

    [Fact]
    public void A_window_that_would_stick_out_is_pulled_back_inside_the_screen()
    {
        var (left, top) = ToastPlacement.Resolve(1800, 950, 340, 200, Work, Virtual);

        Assert.Equal(1920 - 340, left);
        Assert.Equal(1080 - 200, top);
    }

    [Fact]
    public void A_position_that_leaves_almost_nothing_visible_counts_as_lost()
    {
        var (left, top) = ToastPlacement.Resolve(1800, 1050, 340, 200, Work, Virtual);

        Assert.Equal(1920 - 340 - 16, left);
        Assert.Equal(16, top);
    }

    [Fact]
    public void A_second_monitor_on_the_left_with_negative_coordinates_is_supported()
    {
        var wide = new ScreenBounds(-1920, 0, 1920, 1080);

        var (left, top) = ToastPlacement.Resolve(-1500, 100, 340, 90, Work, wide);

        Assert.Equal(-1500, left);
        Assert.Equal(100, top);
    }

    [Theory]
    [InlineData(0.0, 0.2)]
    [InlineData(0.05, 0.2)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(7.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    public void Opacity_stays_visible(double value, double expected) =>
        Assert.Equal(expected, ToastPlacement.ClampOpacity(value));

    [Theory]
    [InlineData(0.0, 0.6)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(9.0, 2.0)]
    [InlineData(double.NaN, 1.0)]
    public void Scale_stays_within_limits(double value, double expected) =>
        Assert.Equal(expected, ToastPlacement.ClampScale(value));

    [Fact]
    public void The_defaults_match_the_old_look_and_the_choices_survive_a_restart()
    {
        var defaults = new AppSettings();
        Assert.Equal(1.0, defaults.ToastOpacity);
        Assert.Equal(1.0, defaults.ToastScale);
        Assert.Null(defaults.ToastLeft);
        Assert.Null(defaults.ToastTop);

        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ToastOpacity = 0.55;
        store.Current.ToastScale = 1.4;
        store.Current.ToastLeft = -1500.5;
        store.Current.ToastTop = 120;
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal(0.55, reloaded.ToastOpacity);
        Assert.Equal(1.4, reloaded.ToastScale);
        Assert.Equal(-1500.5, reloaded.ToastLeft);
        Assert.Equal(120, reloaded.ToastTop);
    }

    [Fact]
    public void An_old_settings_file_without_the_new_fields_still_loads_with_the_old_look()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"PlaySound\":false}");

        var settings = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal(1.0, settings.ToastOpacity);
        Assert.Equal(1.0, settings.ToastScale);
        Assert.Null(settings.ToastLeft);
    }

    [Theory]
    [InlineData(6, 6)]
    [InlineData(2, 2)]
    [InlineData(60, 60)]
    [InlineData(0, 2)]
    [InlineData(-5, 2)]
    [InlineData(1, 2)]
    [InlineData(61, 60)]
    [InlineData(10000, 60)]
    [InlineData(7.4, 7)]
    [InlineData(7.6, 8)]
    public void The_time_on_screen_is_kept_between_two_and_sixty_seconds(double value, int expected) =>
        Assert.Equal(expected, ToastPlacement.ClampSeconds(value));

    [Fact]
    public void A_missing_or_broken_duration_falls_back_to_the_old_six_seconds() =>
        Assert.Equal(6, ToastPlacement.ClampSeconds(double.NaN));

    [Fact]
    public void The_time_on_screen_defaults_to_six_seconds_and_survives_a_restart()
    {
        Assert.Equal(6, new AppSettings().ToastSeconds);

        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ToastSeconds = 15;
        store.Save();

        Assert.Equal(15, new SettingsStore(dir.File("settings.json")).Current.ToastSeconds);
    }

    [Fact]
    public void An_old_settings_file_with_the_removed_sound_option_still_loads_and_keeps_six_seconds()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"PlaySound\":true,\"ShowToast\":false}");

        var settings = new SettingsStore(dir.File("settings.json")).Current;

        Assert.False(settings.ShowToast);
        Assert.Equal(6, settings.ToastSeconds);
    }}
