using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatHighlightTests
{
    [Theory]
    [InlineData("#ff9f0a", "#FF9F0A")]
    [InlineData("FF9F0A", "#FF9F0A")]
    [InlineData("  #0a84ff  ", "#0A84FF")]
    [InlineData("#f90", "#FF9900")]
    [InlineData("abc", "#AABBCC")]
    public void Colours_are_accepted_in_the_usual_spellings_and_kept_as_six_capital_digits(string typed, string expected)
    {
        Assert.Equal(expected, ChatHighlights.Normalize(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("#1234")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("красный")]
    public void Anything_that_is_not_a_colour_is_refused(string? typed)
    {
        Assert.Null(ChatHighlights.Normalize(typed));
    }

    [Fact]
    public void A_colour_is_split_into_its_three_bytes()
    {
        Assert.Equal(((byte)0xFF, (byte)0x9F, (byte)0x0A), ChatHighlights.Parse("#FF9F0A"));
        Assert.Equal(((byte)0, (byte)0, (byte)0), ChatHighlights.Parse("#000000"));
        Assert.Equal(((byte)255, (byte)255, (byte)255), ChatHighlights.Parse("#FFFFFF"));
    }

    [Fact]
    public void The_palette_has_distinct_named_colours_that_are_all_valid()
    {
        Assert.True(ChatHighlights.Palette.Count >= 6);
        Assert.Equal(ChatHighlights.Palette.Count, ChatHighlights.Palette.Select(s => s.Hex).Distinct().Count());
        Assert.Equal(ChatHighlights.Palette.Count, ChatHighlights.Palette.Select(s => s.Name).Distinct().Count());
        foreach (var swatch in ChatHighlights.Palette)
        {
            Assert.Equal(swatch.Hex, ChatHighlights.Normalize(swatch.Hex));
            Assert.False(string.IsNullOrWhiteSpace(swatch.Name));
        }
    }

    [Fact]
    public void Nobody_is_highlighted_by_default_and_the_marks_survive_a_restart()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        Assert.Empty(store.Current.ChatHighlights);
        store.Current.ChatHighlights["42"] = "#FF9F0A";
        store.Current.ChatHighlights["viewer"] = "#0A84FF";
        store.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current.ChatHighlights;

        Assert.Equal("#FF9F0A", reloaded["42"]);
        Assert.Equal("#0A84FF", reloaded["viewer"]);
    }

    [Theory]
    [InlineData(0, 1, 1, "#FF0000")]
    [InlineData(60, 1, 1, "#FFFF00")]
    [InlineData(120, 1, 1, "#00FF00")]
    [InlineData(180, 1, 1, "#00FFFF")]
    [InlineData(240, 1, 1, "#0000FF")]
    [InlineData(300, 1, 1, "#FF00FF")]
    [InlineData(0, 0, 1, "#FFFFFF")]
    [InlineData(200, 0, 0.5, "#808080")]
    [InlineData(123, 1, 0, "#000000")]
    [InlineData(0, 0.5, 1, "#FF8080")]
    public void The_palette_squares_give_the_expected_colours(double h, double s, double v, string expected)
    {
        var (r, g, b) = ChatHighlights.FromHsv(h, s, v);
        Assert.Equal(expected, ChatHighlights.ToHex(r, g, b));
    }

    [Fact]
    public void A_hue_outside_the_circle_wraps_round_and_the_other_two_are_kept_inside_their_range()
    {
        Assert.Equal(ChatHighlights.FromHsv(330, 1, 1), ChatHighlights.FromHsv(-30, 1, 1));
        Assert.Equal(ChatHighlights.FromHsv(0, 1, 1), ChatHighlights.FromHsv(360, 1, 1));
        Assert.Equal(ChatHighlights.FromHsv(10, 1, 1), ChatHighlights.FromHsv(370, 1, 1));
        Assert.Equal(ChatHighlights.FromHsv(40, 1, 1), ChatHighlights.FromHsv(40, 5, 9));
        Assert.Equal(ChatHighlights.FromHsv(40, 0, 0), ChatHighlights.FromHsv(40, -5, -9));
    }

    [Fact]
    public void Every_palette_colour_survives_the_trip_through_the_picker()
    {
        foreach (var swatch in ChatHighlights.Palette)
        {
            var (r, g, b) = ChatHighlights.Parse(swatch.Hex);
            var (h, s, v) = ChatHighlights.ToHsv(r, g, b);
            var (r2, g2, b2) = ChatHighlights.FromHsv(h, s, v);
            Assert.Equal(swatch.Hex, ChatHighlights.ToHex(r2, g2, b2));
        }
    }

    [Fact]
    public void Greys_and_black_have_no_hue_to_speak_of()
    {
        Assert.Equal((0.0, 0.0, 0.0), ChatHighlights.ToHsv(0, 0, 0));
        var grey = ChatHighlights.ToHsv(128, 128, 128);
        Assert.Equal(0.0, grey.H);
        Assert.Equal(0.0, grey.S);
        var red = ChatHighlights.ToHsv(255, 0, 0);
        Assert.Equal((0.0, 1.0, 1.0), red);
        Assert.Equal(120.0, ChatHighlights.ToHsv(0, 255, 0).H);
        Assert.Equal(240.0, ChatHighlights.ToHsv(0, 0, 255).H);
    }
}