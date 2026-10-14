using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class FullscreenWatchTests
{
    private static NativeMethods.Rect R(int left, int top, int right, int bottom) => new() { Left = left, Top = top, Right = right, Bottom = bottom };

    [Fact]
    public void A_window_that_covers_its_whole_monitor_counts_as_fullscreen()
    {
        var monitor = R(0, 0, 1920, 1080);

        Assert.True(FullscreenWatch.CoversMonitor(R(0, 0, 1920, 1080), monitor));
        Assert.True(FullscreenWatch.CoversMonitor(R(-8, -8, 1928, 1088), monitor));
        Assert.False(FullscreenWatch.CoversMonitor(R(-8, -8, 1928, 1048), monitor));
        Assert.False(FullscreenWatch.CoversMonitor(R(100, 100, 900, 700), monitor));
        Assert.True(FullscreenWatch.CoversMonitor(R(1920, 0, 3840, 1080), R(1920, 0, 3840, 1080)));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("XamlExplorerHostIslandWindow", true)]
    [InlineData("MultitaskingViewFrame", true)]
    [InlineData("UnityWndClass", false)]
    [InlineData("", false)]
    public void The_desktop_and_the_task_switcher_are_not_taken_for_a_game(string windowClass, bool shell)
    {
        Assert.Equal(shell, FullscreenWatch.IsShell(windowClass));
    }

    [Fact]
    public void The_overlays_are_shown_again_right_after_the_game_comes_back_and_once_more_when_it_settles()
    {
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2) }, FullscreenWatch.RecheckAfter);
    }
}
