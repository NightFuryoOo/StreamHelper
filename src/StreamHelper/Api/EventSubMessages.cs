using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using StreamHelper.Models;

namespace StreamHelper.Api;

public sealed record EventSubMessage(
    string Type,
    string MessageId,
    string SessionId,
    int KeepaliveSeconds,
    string? ReconnectUrl,
    string SubscriptionType,
    Subscriber? Subscriber,
    Redemption? Redemption,
    ChatMessage? Chat = null,
    ChannelMoment? Moment = null);

public sealed record ChatMention(string UserId, string Login, string Name);

public sealed record ChatBadge(string SetId, string Id);

public sealed record ChatFragment(string Type, string Text, string EmoteId = "", bool Animated = false);

public sealed record ChatMessage(
    string MessageId, string ChatterId, string ChatterLogin, string ChatterName, string Text,
    IReadOnlyList<ChatMention> Mentions, DateTime AtUtc, string Color = "", IReadOnlyList<ChatBadge>? Badges = null,
    IReadOnlyList<ChatFragment>? Fragments = null);

public static class EventSubParser
{
    public const string RedemptionType = "channel.channel_points_custom_reward_redemption.add";
    public const string ChatMessageType = "channel.chat.message";
    public const string ChatNoticeType = "channel.chat.notification";

    public static EventSubMessage? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("metadata", out var metadata)) return null;

            var type = ReadString(metadata, "message_type");
            var messageId = ReadString(metadata, "message_id");
            var timestamp = ReadTimestamp(metadata, "message_timestamp");
            var payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;

            var sessionId = "";
            var keepalive = 0;
            string? reconnect = null;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("session", out var session) &&
                session.ValueKind == JsonValueKind.Object)
            {
                sessionId = ReadString(session, "id");
                keepalive = ReadInt(session, "keepalive_timeout_seconds");
                var url = ReadString(session, "reconnect_url");
                reconnect = url.Length == 0 ? null : url;
            }

            var subscriptionType = ReadString(metadata, "subscription_type");
            Subscriber? subscriber = null;
            Redemption? redemption = null;
            ChatMessage? chat = null;
            ChannelMoment? moment = null;
            if (type == "notification" && payload.ValueKind == JsonValueKind.Object &&
                payload.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.Object)
            {
                if (subscriptionType == RedemptionType) redemption = BuildRedemption(messageId, timestamp, ev);
                else if (subscriptionType == ChatMessageType) chat = BuildChat(messageId, timestamp, ev);
                else if (subscriptionType == ChatNoticeType) moment = BuildMoment(messageId, timestamp, ev);
                else subscriber = BuildSubscriber(subscriptionType, messageId, timestamp, ev);
            }

            return new EventSubMessage(type, messageId, sessionId, keepalive, reconnect, subscriptionType, subscriber, redemption, chat, moment);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static ChatMessage BuildChat(string messageId, DateTime messageAt, JsonElement ev)
    {
        var text = "";
        var mentions = new List<ChatMention>();
        var parts = new List<ChatFragment>();
        if (ev.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            text = ReadString(message, "text");
            if (message.TryGetProperty("fragments", out var fragments) && fragments.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in fragments.EnumerateArray())
                {
                    if (fragment.ValueKind != JsonValueKind.Object) continue;
                    var type = ReadString(fragment, "type");
                    var piece = ReadString(fragment, "text");
                    if (type == "emote" && fragment.TryGetProperty("emote", out var emote) && emote.ValueKind == JsonValueKind.Object && ReadString(emote, "id").Length > 0)
                    {
                        var animated = false;
                        if (emote.TryGetProperty("format", out var formats) && formats.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var format in formats.EnumerateArray())
                            {
                                if (format.ValueKind == JsonValueKind.String && format.GetString() == "animated") animated = true;
                            }
                        }
                        parts.Add(new ChatFragment("emote", piece, ReadString(emote, "id"), animated));
                        continue;
                    }
                    parts.Add(new ChatFragment("text", piece));
                    if (type != "mention") continue;
                    if (!fragment.TryGetProperty("mention", out var mention) || mention.ValueKind != JsonValueKind.Object) continue;
                    mentions.Add(new ChatMention(ReadString(mention, "user_id"), ReadString(mention, "user_login"), ReadString(mention, "user_name")));
                }
            }
        }

        var badges = new List<ChatBadge>();
        if (ev.TryGetProperty("badges", out var badgeList) && badgeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badgeList.EnumerateArray())
            {
                if (badge.ValueKind != JsonValueKind.Object) continue;
                var setId = ReadString(badge, "set_id");
                if (setId.Length > 0) badges.Add(new ChatBadge(setId, ReadString(badge, "id")));
            }
        }

        var chatMessageId = ReadString(ev, "message_id");
        return new ChatMessage(
            chatMessageId.Length > 0 ? chatMessageId : messageId,
            ReadString(ev, "chatter_user_id"), ReadString(ev, "chatter_user_login"), ReadString(ev, "chatter_user_name"),
            text, mentions, messageAt, ReadString(ev, "color"), badges, parts.Count > 0 ? parts : null);
    }

    private static ChannelMoment? BuildMoment(string messageId, DateTime at, JsonElement ev)
    {
        var id = ReadString(ev, "message_id");
        var key = id.Length > 0 ? id : messageId;
        switch (ReadString(ev, "notice_type"))
        {
            case "raid" when ev.TryGetProperty("raid", out var raid) && raid.ValueKind == JsonValueKind.Object:
                return new ChannelMoment
                {
                    Key = key,
                    Kind = MomentKind.Raid,
                    Login = ReadString(raid, "user_login"),
                    DisplayName = ReadString(raid, "user_name"),
                    Viewers = ReadInt(raid, "viewer_count"),
                    AtUtc = at,
                };
            case "watch_streak" when ev.TryGetProperty("watch_streak", out var streak) && streak.ValueKind == JsonValueKind.Object:
                var text = ev.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object ? ReadString(message, "text") : "";
                return new ChannelMoment
                {
                    Key = key,
                    Kind = MomentKind.Streak,
                    Login = ReadString(ev, "chatter_user_login"),
                    DisplayName = ReadString(ev, "chatter_user_name"),
                    StreakCount = ReadInt(streak, "streak_count"),
                    ChannelPoints = ReadInt(streak, "channel_points_awarded"),
                    Message = text,
                    AtUtc = at,
                };
            default:
                return null;
        }
    }

    private static Redemption BuildRedemption(string messageId, DateTime messageAt, JsonElement ev)
    {
        var id = ReadString(ev, "id");
        var redemption = new Redemption
        {
            Key = id.Length > 0 ? id : messageId,
            Login = ReadString(ev, "user_login"),
            DisplayName = ReadString(ev, "user_name"),
            UserInput = ReadString(ev, "user_input"),
            AtUtc = ReadTimestamp(ev, "redeemed_at", messageAt),
        };

        var status = ReadString(ev, "status");
        if (status.Length > 0 && !status.Equals("unfulfilled", StringComparison.OrdinalIgnoreCase))
        {
            redemption.Status = RedemptionStatus.Processed;
        }
        if (ev.TryGetProperty("reward", out var reward) && reward.ValueKind == JsonValueKind.Object)
        {
            redemption.RewardId = ReadString(reward, "id");
            redemption.RewardTitle = ReadString(reward, "title");
            redemption.Cost = reward.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number && cost.TryGetInt64(out var c) ? c : 0;
        }
        return redemption;
    }

    private static Subscriber? BuildSubscriber(string subscriptionType, string messageId, DateTime at, JsonElement ev)
    {
        var subscriber = new Subscriber
        {
            Key = messageId,
            Login = ReadString(ev, "user_login"),
            DisplayName = ReadString(ev, "user_name"),
            Tier = ReadString(ev, "tier"),
            AtUtc = at,
        };

        switch (subscriptionType)
        {
            case "channel.subscribe":
                if (ReadBool(ev, "is_gift")) return null;
                subscriber.Kind = SubscriptionKind.New;
                return subscriber;

            case "channel.subscription.message":
                subscriber.Kind = SubscriptionKind.Resub;
                subscriber.Months = ReadInt(ev, "cumulative_months");
                subscriber.StreakMonths = ReadInt(ev, "streak_months");
                if (ev.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    subscriber.Message = ReadString(message, "text");
                }
                return subscriber;

            case "channel.subscription.gift":
                subscriber.Kind = SubscriptionKind.Gift;
                subscriber.GiftTotal = ReadInt(ev, "total");
                subscriber.IsAnonymous = ReadBool(ev, "is_anonymous");
                return subscriber;

            default:
                return null;
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTime ReadTimestamp(JsonElement element, string name) => ReadTimestamp(element, name, DateTime.UtcNow);

    private static DateTime ReadTimestamp(JsonElement element, string name, DateTime fallback)
    {
        var text = ReadString(element, name);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : fallback;
    }
}
