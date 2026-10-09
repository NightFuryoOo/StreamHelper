using System.Collections.Generic;
using StreamHelper.Models;

namespace StreamHelper.Ui;

public sealed record ToastText(string Title, string Line, string? Message);

public static class EventToasts
{
    public static ToastText Donations(IReadOnlyList<Donation> added)
    {
        var newest = added[^1];
        return new ToastText(
            added.Count == 1 ? "Новый донат" : $"Новых донатов: {added.Count}",
            $"{newest.Username} · {newest.AmountText}", newest.Message);
    }

    public static ToastText Followers(IReadOnlyList<Follower> added) =>
        new(added.Count == 1 ? "Новый фолловер" : $"Новых фолловеров: {added.Count}", added[^1].Name, null);

    public static ToastText Subscribers(IReadOnlyList<Subscriber> added)
    {
        var newest = added[^1];
        var line = string.IsNullOrEmpty(newest.DetailText) ? newest.Name : $"{newest.Name} · {newest.DetailText}";
        return new ToastText(added.Count == 1 ? newest.ToastTitle : $"Новых подписок: {added.Count}", line, newest.Message);
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
}
