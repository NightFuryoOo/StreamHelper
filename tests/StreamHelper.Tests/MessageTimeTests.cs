using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class MessageTimeTests
{
    [Fact]
    public void Message_times_are_off_until_switched_on_and_travel_with_the_profile()
    {
        Assert.False(new AppSettings().ChatShowTime);
        Assert.Contains(nameof(AppSettings.ChatShowTime), Profile.SharedSettings);
    }

    [Fact]
    public void The_time_is_local_hours_and_minutes()
    {
        var at = new DateTime(2026, 11, 9, 23, 5, 59, DateTimeKind.Utc);

        Assert.Equal(at.ToLocalTime().ToString("HH:mm"), ChatPlacement.MessageTime(at));
        Assert.Matches(@"^\d\d:\d\d$", ChatPlacement.MessageTime(at));
    }

    [Fact]
    public void The_time_is_a_bit_smaller_than_the_chat_text()
    {
        Assert.Equal(12.8, ChatPlacement.TimeSize(15));
        Assert.True(ChatPlacement.TimeSize(24) < 24);
    }
}
