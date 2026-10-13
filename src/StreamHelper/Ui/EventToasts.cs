using System.Collections.Generic;
using System.Linq;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed record ToastText(string Title, string Line, string? Message);

public static class EventToasts
{
    public static ToastText Donations(IReadOnlyList<Donation> added)
    {
        var newest = added[^1];
        var video = newest.HasVideo ? "▶ " + newest.VideoTitle : null;
        var message = video == null ? newest.Message : newest.HasMessage ? newest.Message + "\n" + video : video;
        return new ToastText(
            added.Count == 1 ? "Новый донат" : $"Новых донатов: {added.Count}",
            $"{newest.Username} · {newest.AmountText}", message);
    }

    public static ToastText Followers(IReadOnlyList<Follower> added) =>
        new(added.Count == 1 ? "Новый фолловер" : $"Новых фолловеров: {added.Count}", added[^1].Name, null);

    public static bool Notifies(AppSettings settings, Subscriber subscriber) =>
        subscriber.IsBits ? settings.NotifyBits : settings.NotifySubscribers;

    public static ToastText Subscribers(IReadOnlyList<Subscriber> added)
    {
        var newest = added[^1];
        var line = string.IsNullOrEmpty(newest.DetailText) ? newest.Name : $"{newest.Name} · {newest.DetailText}";
        var bits = added.Count(s => s.IsBits);
        var title = added.Count == 1 ? newest.ToastTitle
            : bits == added.Count ? $"Bits: {added.Count}"
            : bits == 0 ? $"Новых подписок: {added.Count}"
            : $"Подписок и Bits: {added.Count}";
        return new ToastText(title, line, newest.Message);
    }

    public static ToastText Redemptions(IReadOnlyList<Redemption> added)
    {
        var newest = added[^1];
        return new ToastText(
            added.Count == 1 ? $"Награда: {newest.Title}" : $"Новых наград: {added.Count}",
            $"{newest.Name} · {newest.CostText}", newest.UserInput);
    }

    public static ToastText Pings(IReadOnlyList<ChatPing> added)
    {
        var newest = added[^1];
        return new ToastText(added.Count == 1 ? "Тебя упомянули в чате" : $"Упоминаний в чате: {added.Count}", newest.Name, newest.Message);
    }

    public static bool Notifies(AppSettings settings, ChannelMoment moment) =>
        moment.IsRaid ? settings.NotifyRaids : settings.NotifyStreaks;

    public static ToastText Moments(IReadOnlyList<ChannelMoment> added)
    {
        var newest = added[^1];
        var raids = added.Count(m => m.IsRaid);
        var title = added.Count == 1
            ? newest.IsRaid ? "Рейд на канал" : "Серия просмотров"
            : raids == added.Count ? $"Рейдов: {raids}"
            : raids == 0 ? $"Стриков: {added.Count}"
            : $"Рейдов и стриков: {added.Count}";
        var line = newest.IsRaid
            ? $"{newest.Name} · {newest.Detail}"
            : $"{newest.Name} · {newest.StreakCount} {Words.Plural(newest.StreakCount, "стрим", "стрима", "стримов")} подряд";
        return new ToastText(title, line, newest.HasMessage ? newest.Message : null);
    }
}
