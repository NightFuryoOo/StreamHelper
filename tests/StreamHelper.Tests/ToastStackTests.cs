using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ToastStackTests
{
    private static readonly ScreenBounds Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Toasts_go_down_one_under_another_from_the_chosen_place()
    {
        var tops = ToastStack.Tops(16, new double[] { 80, 100, 80, 80 }, Work);

        Assert.Equal(new double[] { 16, 104, 212, 300 }, tops.ToArray());
    }

    [Fact]
    public void Near_the_bottom_of_the_screen_the_oldest_stays_and_the_newer_ones_go_up()
    {
        var tops = ToastStack.Tops(900, new double[] { 80, 80, 80 }, Work);

        Assert.Equal(new double[] { 900, 812, 724 }, tops.ToArray());
    }

    [Fact]
    public void One_toast_stays_where_it_was_put_and_none_gives_nothing()
    {
        Assert.Equal(new double[] { 950 }, ToastStack.Tops(950, new double[] { 80 }, Work).ToArray());
        Assert.Empty(ToastStack.Tops(16, Array.Empty<double>(), Work));
        Assert.Equal(4, ToastStack.MaxToasts);
    }

    [Fact]
    public void Going_up_never_leaves_the_top_of_the_screen()
    {
        var tops = ToastStack.Tops(200, new double[] { 900, 300 }, new ScreenBounds(0, 0, 1920, 1040));

        Assert.All(tops, top => Assert.True(top >= 0));
    }
}