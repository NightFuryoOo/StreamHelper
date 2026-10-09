using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class RewardListener : EventSubListener<Redemption>
{
    public static readonly string[] RewardTypes = { EventSubParser.RedemptionType };

    private static readonly EventSubFeature Feature = new(
        "Награды",
        TwitchClient.RedemptionScope,
        RewardTypes,
        "Для наград за баллы переподключи Twitch в настройках, вкладка «Twitch»: нужно новое право.");

    public RewardListener(SettingsStore settings, IEventSubApi api, string url, Func<IReadOnlyList<Redemption>, Task> deliver)
        : base(settings, api, url, Feature, deliver)
    {
    }

    protected override Redemption? Select(EventSubMessage message) => message.Redemption;
}
