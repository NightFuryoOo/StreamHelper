using StreamHelper.Api;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RewardOrderTests
{
    [Fact]
    public void The_own_rewards_go_from_the_cheapest_and_the_same_price_by_name()
    {
        var rewards = new[]
        {
            new RewardInfo("a", "Вебка​", 40001, true),
            new RewardInfo("b", "МИМИМУА", 99, true),
            new RewardInfo("c", "Место", 1, false),
            new RewardInfo("d", "бета", 2500, true),
            new RewardInfo("e", "Альфа", 2500, true),
        };

        Assert.Equal(new[] { "c", "b", "e", "d", "a" }, RewardOrder.ByCost(rewards).Select(r => r.Id).ToArray());
    }
}
