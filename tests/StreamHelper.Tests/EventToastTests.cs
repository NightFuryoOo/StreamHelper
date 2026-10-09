using StreamHelper.Models;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class EventToastTests
{
    private static readonly DateTime At = new(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void One_donation_names_the_donor_and_amount_and_keeps_the_message()
    {
        var donation = new Donation { Id = 1, Username = "Иван", Amount = 500, Currency = "RUB", Message = "Привет", CreatedAtUtc = At };

        var text = EventToasts.Donations(new[] { donation });

        Assert.Equal("Новый донат", text.Title);
        Assert.Equal($"Иван · {donation.AmountText}", text.Line);
        Assert.Equal("Привет", text.Message);
    }

    [Fact]
    public void Several_donations_are_counted_and_the_newest_is_shown()
    {
        var older = new Donation { Id = 1, Username = "Анна", Amount = 50, Currency = "USD", CreatedAtUtc = At };
        var newest = new Donation { Id = 2, Username = "Марк", Amount = 10, Currency = "RUB", Message = "", CreatedAtUtc = At };

        var text = EventToasts.Donations(new[] { older, newest });

        Assert.Equal("Новых донатов: 2", text.Title);
        Assert.Equal($"Марк · {newest.AmountText}", text.Line);
        Assert.Equal("", text.Message);
    }

    [Fact]
    public void Followers_show_the_newest_name_without_a_message()
    {
        var a = new Follower { UserId = "1", Login = "lisa", DisplayName = "Lisa", FollowedAtUtc = At };
        var b = new Follower { UserId = "2", Login = "mark", DisplayName = "Mark", FollowedAtUtc = At };

        Assert.Equal(new ToastText("Новый фолловер", a.Name, null), EventToasts.Followers(new[] { a }));
        Assert.Equal(new ToastText("Новых фолловеров: 2", b.Name, null), EventToasts.Followers(new[] { a, b }));
    }

    [Fact]
    public void Subscriptions_add_the_details_when_there_are_any()
    {
        var resub = new Subscriber
        {
            Key = "s1", Kind = SubscriptionKind.Resub, Login = "old_friend", DisplayName = "Old_Friend", Tier = "2000",
            Months = 7, StreakMonths = 4, Message = "Спасибо", AtUtc = At,
        };
        var gift = new Subscriber { Key = "s2", Kind = SubscriptionKind.Gift, IsAnonymous = true, Tier = "1000", GiftTotal = 5, AtUtc = At };

        var one = EventToasts.Subscribers(new[] { resub });
        var two = EventToasts.Subscribers(new[] { resub, gift });

        Assert.Equal(resub.ToastTitle, one.Title);
        Assert.Equal(string.IsNullOrEmpty(resub.DetailText) ? resub.Name : $"{resub.Name} · {resub.DetailText}", one.Line);
        Assert.Contains(" · ", one.Line);
        Assert.Equal("Спасибо", one.Message);
        Assert.Equal("Новых подписок: 2", two.Title);
        Assert.Equal(string.IsNullOrEmpty(gift.DetailText) ? gift.Name : $"{gift.Name} · {gift.DetailText}", two.Line);
    }

    [Fact]
    public void Rewards_name_the_reward_when_alone_and_show_viewer_cost_and_text()
    {
        var order = new Redemption
        {
            Key = "r1", Login = "mark", DisplayName = "Mark", RewardId = "rw", RewardTitle = "Заказать трек", Cost = 500,
            UserInput = "Hollow Knight", AtUtc = At,
        };
        var other = new Redemption { Key = "r2", Login = "anna", DisplayName = "Anna", RewardId = "rw", RewardTitle = "Эмодзи", Cost = 100, AtUtc = At };

        var one = EventToasts.Redemptions(new[] { order });
        var two = EventToasts.Redemptions(new[] { order, other });

        Assert.Equal($"Награда: {order.Title}", one.Title);
        Assert.Equal($"{order.Name} · {order.CostText}", one.Line);
        Assert.Equal("Hollow Knight", one.Message);
        Assert.Equal("Новых наград: 2", two.Title);
        Assert.Equal($"{other.Name} · {other.CostText}", two.Line);
    }

    [Fact]
    public void Pings_say_who_mentioned_you_and_what_they_wrote()
    {
        var ping = new ChatPing { Key = "p1", Login = "anna_k", DisplayName = "Anna_K", Message = "@streamer эй", AtUtc = At };
        var second = new ChatPing { Key = "p2", Login = "mark", DisplayName = "Mark", Message = "ты тут?", AtUtc = At };

        Assert.Equal(new ToastText("Тебя упомянули в чате", ping.Name, "@streamer эй"), EventToasts.Pings(new[] { ping }));
        Assert.Equal(new ToastText("Упоминаний в чате: 2", second.Name, "ты тут?"), EventToasts.Pings(new[] { ping, second }));
    }
}
