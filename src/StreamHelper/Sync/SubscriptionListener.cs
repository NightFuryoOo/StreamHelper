using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class SubscriptionListener : EventSubListener<Subscriber>
{
    public static readonly string[] SubscriptionTypes =
    {
        "channel.subscribe",
        "channel.subscription.message",
        "channel.subscription.gift",
    };

    private static readonly EventSubFeature Feature = new(
        "Подписки",
        TwitchClient.SubscriptionScope,
        SubscriptionTypes,
        "Для подписок переподключи Twitch в настройках, вкладка «Twitch»: нужно новое право.");

    public SubscriptionListener(SettingsStore settings, IEventSubApi api, string url, Func<IReadOnlyList<Subscriber>, Task> deliver)
        : base(settings, api, url, Feature, deliver)
    {
    }

    protected override Subscriber? Select(EventSubMessage message) => message.Subscriber;
}
