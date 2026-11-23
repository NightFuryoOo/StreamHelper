using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public enum ArchiveFilter
{
    Messages,
    Donations,
    Subscriptions,
    Rewards,
    Raids,
    Clips,
}

public enum ArchiveEventTone
{
    Donation,
    Subscription,
    Gift,
    Bits,
    Reward,
    Raid,
    Clip,
}

public sealed record ArchiveEventText(
    string Label, ArchiveEventTone Tone, string Head, string Body,
    string VideoTitle = "", string VideoUrl = "", string VideoThumbnail = "", string VideoLabel = "", bool VideoIsClip = false);

public sealed class ArchiveRow
{
    public ArchiveRow(ChatMessage chat, bool deleted, ChatModeration? moderation)
    {
        Chat = chat;
        AtUtc = chat.AtUtc;
        Filter = ArchiveFilter.Messages;
        Key = "m:" + (chat.MessageId.Length > 0 ? chat.MessageId : chat.AtUtc.Ticks + ":" + chat.ChatterLogin);
        Deleted = deleted;
        Moderation = moderation;
    }

    public ArchiveRow(ArchiveLine line, DateTime fallbackUtc)
    {
        Line = line;
        AtUtc = line.AtUtc != default ? line.AtUtc : fallbackUtc;
        Filter = line.Kind switch
        {
            ArchiveKind.Donation => ArchiveFilter.Donations,
            ArchiveKind.Subscriber => ArchiveFilter.Subscriptions,
            ArchiveKind.Redemption => ArchiveFilter.Rewards,
            ArchiveKind.Clip => ArchiveFilter.Clips,
            _ => ArchiveFilter.Raids,
        };
        Event = ArchiveTimeline.Describe(line);
        Key = line.Kind switch
        {
            ArchiveKind.Donation => "d:" + line.Donation!.Key,
            ArchiveKind.Subscriber => "s:" + line.Subscriber!.Key,
            ArchiveKind.Redemption => "r:" + line.Redemption!.Key,
            ArchiveKind.Clip => "c:" + line.Clip!.Id,
            _ => "e:" + line.Moment!.Key,
        };
    }

    public string Key { get; }
    public DateTime AtUtc { get; }
    public ArchiveFilter Filter { get; }
    public ChatMessage? Chat { get; }
    public ArchiveLine? Line { get; }
    public ArchiveEventText? Event { get; }
    public bool Deleted { get; set; }
    public ChatModeration? Moderation { get; set; }

    public string SearchText => Chat is { } chat
        ? chat.ChatterName + " " + chat.ChatterLogin + " " + chat.Text
        : Event is { } text ? text.Head + " " + text.Body + " " + text.VideoTitle : "";
}

public static class ArchiveTimeline
{
    public static readonly ArchiveFilter[] AllFilters = Enum.GetValues<ArchiveFilter>();

    public static List<ArchiveRow> Build(ArchiveContent content, DateTime streamStartUtc)
    {
        var rows = new List<ArchiveRow>(content.Messages.Count + 16);
        foreach (var message in content.Messages)
        {
            rows.Add(new ArchiveRow(message, content.Deleted.Contains(message.MessageId), content.Moderation.GetValueOrDefault(message.MessageId)));
        }
        foreach (var donation in content.Donations) rows.Add(new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Donation, AtUtc = donation.CreatedAtUtc, Donation = donation }, streamStartUtc));
        foreach (var subscriber in content.Subscribers) rows.Add(new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Subscriber, AtUtc = subscriber.AtUtc, Subscriber = subscriber }, streamStartUtc));
        foreach (var redemption in content.Redemptions) rows.Add(new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Redemption, AtUtc = redemption.AtUtc, Redemption = redemption }, streamStartUtc));
        foreach (var moment in content.Moments) rows.Add(new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Moment, AtUtc = moment.AtUtc, Moment = moment }, streamStartUtc));
        foreach (var clip in content.Clips) rows.Add(new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Clip, AtUtc = clip.CreatedUtc, Clip = clip }, streamStartUtc));
        return rows.OrderBy(r => r.AtUtc).ToList();
    }

    public static int PositionOf(IReadOnlyList<ArchiveRow> rows, DateTime atUtc)
    {
        var index = rows.Count;
        while (index > 0 && rows[index - 1].AtUtc > atUtc) index--;
        return index;
    }

    public static ArchiveRow? RowOf(ArchiveLine line, DateTime streamStartUtc) => line.Kind switch
    {
        ArchiveKind.Chat when line.Chat != null => new ArchiveRow(line.Chat, false, null),
        ArchiveKind.Donation when line.Donation != null => new ArchiveRow(line, streamStartUtc),
        ArchiveKind.Subscriber when line.Subscriber != null => new ArchiveRow(line, streamStartUtc),
        ArchiveKind.Redemption when line.Redemption != null => new ArchiveRow(line, streamStartUtc),
        ArchiveKind.Moment when line.Moment != null => new ArchiveRow(line, streamStartUtc),
        ArchiveKind.Clip when line.Clip != null => new ArchiveRow(line, streamStartUtc),
        _ => null,
    };

    public static bool Matches(ArchiveRow row, IReadOnlySet<ArchiveFilter> filters, string search)
    {
        if (!filters.Contains(row.Filter)) return false;
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return true;
        var text = row.SearchText;
        return words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
    }

    public static ArchiveEventText? Describe(ArchiveLine line)
    {
        switch (line.Kind)
        {
            case ArchiveKind.Donation when line.Donation is { } donation:
                return new ArchiveEventText("Донат", ArchiveEventTone.Donation, Join(donation.DisplayName, donation.AmountText), donation.Message.Trim(),
                    donation.HasVideo ? donation.VideoTitle ?? donation.VideoUrl ?? "" : "", donation.VideoUrl ?? "",
                    donation.HasVideo ? donation.VideoThumbnail ?? "" : "", donation.HasVideo ? donation.VideoLabel : "");
            case ArchiveKind.Subscriber when line.Subscriber is { } subscriber:
                return subscriber.Kind switch
                {
                    SubscriptionKind.Gift => new ArchiveEventText("Подарок", ArchiveEventTone.Gift, GiftHead(subscriber), Recipients(subscriber)),
                    SubscriptionKind.Bits => new ArchiveEventText(subscriber.PowerUp.Length > 0 ? "Power-up" : "Bits", ArchiveEventTone.Bits,
                        Join(subscriber.Name, subscriber.DetailText), subscriber.Message.Trim()),
                    _ => new ArchiveEventText(subscriber.Kind == SubscriptionKind.Resub ? "Продление" : "Подписка", ArchiveEventTone.Subscription,
                        Join(subscriber.Name, subscriber.DetailText), subscriber.Message.Trim()),
                };
            case ArchiveKind.Redemption when line.Redemption is { } redemption:
                return new ArchiveEventText("Награда", ArchiveEventTone.Reward, Join(redemption.Name, redemption.Title, redemption.CostText), redemption.UserInput.Trim(),
                    redemption.HasVideo ? redemption.VideoTitle ?? redemption.VideoUrl ?? "" : "", redemption.VideoUrl ?? "",
                    redemption.HasVideo ? redemption.VideoThumbnail ?? "" : "", redemption.HasVideo ? redemption.VideoLabel : "", redemption.HasVideo && redemption.VideoIsClip);
            case ArchiveKind.Moment when line.Moment is { } moment:
                return new ArchiveEventText(moment.IsRaid ? "Рейд" : "Стрик", ArchiveEventTone.Raid, Join(moment.Name, moment.Detail), moment.Message.Trim());
            case ArchiveKind.Clip when line.Clip is { } clip:
                return new ArchiveEventText("Клип", ArchiveEventTone.Clip, Join(clip.CreatorName, ClipLength(clip.Seconds)), "",
                    clip.Title.Length > 0 ? clip.Title : clip.Url, clip.Url, clip.ThumbnailUrl, "Клип Twitch", VideoIsClip: true);
            default:
                return null;
        }
    }

    private static string GiftHead(Subscriber subscriber)
    {
        var count = Math.Max(1, subscriber.GiftTotal);
        var what = $"подарил {count} {Words.Plural(count, "подписку", "подписки", "подписок")}";
        return Join(subscriber.Name, what, subscriber.TierText);
    }

    private static string Recipients(Subscriber subscriber)
    {
        if (subscriber.Recipients is not { Count: > 0 } names) return "";
        return "Кому: " + string.Join(", ", names);
    }

    public static string ClipLength(double seconds) => seconds > 0 ? Math.Max(1, (int)Math.Round(seconds)) + " с" : "";

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static string StreamDate(ArchiveStream stream)
    {
        var start = DateTime.SpecifyKind(stream.StartUtc, DateTimeKind.Utc).ToLocalTime();
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return start.ToString("d MMMM yyyy, ddd", culture);
    }

    public static string StreamTime(ArchiveStream stream, bool recording)
    {
        var start = DateTime.SpecifyKind(stream.StartUtc, DateTimeKind.Utc).ToLocalTime();
        if (recording) return start.ToString("HH:mm", CultureInfo.InvariantCulture) + " · идёт запись";
        if (stream.EndUtc is not { } end) return start.ToString("HH:mm", CultureInfo.InvariantCulture);
        var local = DateTime.SpecifyKind(end, DateTimeKind.Utc).ToLocalTime();
        return start.ToString("HH:mm", CultureInfo.InvariantCulture) + "–" + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + Duration(end - stream.StartUtc);
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        if (hours == 0) return $"{minutes} мин";
        return minutes == 0 ? $"{hours} ч" : $"{hours} ч {minutes} мин";
    }

    public static string Counts(ArchiveStream stream)
    {
        var parts = new List<string> { $"{stream.Messages} {Words.Plural(stream.Messages, "сообщение", "сообщения", "сообщений")}" };
        if (stream.Donations > 0) parts.Add($"{stream.Donations} {Words.Plural(stream.Donations, "донат", "доната", "донатов")}");
        if (stream.Subscriptions > 0) parts.Add($"{stream.Subscriptions} {Words.Plural(stream.Subscriptions, "подписка", "подписки", "подписок")}");
        if (stream.Bits > 0) parts.Add("Bits: " + stream.Bits);
        if (stream.Rewards > 0) parts.Add($"{stream.Rewards} {Words.Plural(stream.Rewards, "награда", "награды", "наград")}");
        if (stream.Raids > 0) parts.Add($"{stream.Raids} {Words.Plural(stream.Raids, "рейд", "рейда", "рейдов")}");
        if (stream.Clips > 0) parts.Add($"{stream.Clips} {Words.Plural(stream.Clips, "клип", "клипа", "клипов")}");
        return string.Join(" · ", parts);
    }
}
