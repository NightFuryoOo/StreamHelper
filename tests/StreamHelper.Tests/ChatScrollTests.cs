using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatScrollTests
{
    [Fact]
    public void Scrolling_stops_at_the_newest_and_at_the_oldest_line()
    {
        Assert.Equal(0, ChatPlacement.ClampScroll(-50, 1000, 400));
        Assert.Equal(250, ChatPlacement.ClampScroll(250, 1000, 400));
        Assert.Equal(600, ChatPlacement.ClampScroll(5000, 1000, 400));
        Assert.Equal(0, ChatPlacement.ClampScroll(100, 300, 400));
        Assert.Equal(0, ChatPlacement.ClampScroll(double.NaN, 1000, 400));
    }

    [Fact]
    public void Without_overflow_there_is_no_thumb()
    {
        Assert.Null(ChatPlacement.ScrollThumb(300, 400, 0));
        Assert.Null(ChatPlacement.ScrollThumb(400, 400, 0));
    }

    [Fact]
    public void The_thumb_sits_at_the_bottom_for_the_newest_lines_and_at_the_top_for_the_oldest()
    {
        var newest = ChatPlacement.ScrollThumb(1000, 400, 0)!.Value;
        var oldest = ChatPlacement.ScrollThumb(1000, 400, 600)!.Value;
        var middle = ChatPlacement.ScrollThumb(1000, 400, 300)!.Value;

        Assert.Equal(160, newest.Height);
        Assert.Equal(240, newest.Top);
        Assert.Equal(0, oldest.Top);
        Assert.Equal(120, middle.Top);
    }

    [Fact]
    public void A_very_long_chat_keeps_a_thumb_big_enough_to_grab()
    {
        var thumb = ChatPlacement.ScrollThumb(100000, 300, 0)!.Value;

        Assert.Equal(ChatPlacement.MinScrollThumb, thumb.Height);
        Assert.Equal(300 - ChatPlacement.MinScrollThumb, thumb.Top);
    }

    [Fact]
    public void Dragging_the_thumb_up_shows_older_lines_and_down_newer_ones()
    {
        Assert.Equal(150, ChatPlacement.ScrollAfterThumbDrag(0, -60, 1000, 400));
        Assert.Equal(450, ChatPlacement.ScrollAfterThumbDrag(600, 60, 1000, 400));
        Assert.Equal(600, ChatPlacement.ScrollAfterThumbDrag(500, -1000, 1000, 400));
        Assert.Equal(0, ChatPlacement.ScrollAfterThumbDrag(100, 1000, 1000, 400));
        Assert.Equal(0, ChatPlacement.ScrollAfterThumbDrag(0, -60, 300, 400));
    }
}
