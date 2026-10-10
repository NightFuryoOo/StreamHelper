using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Ui;

public enum ShoutoutBlock
{
    None,
    Recent,
    SameUser,
}

public readonly record struct ShoutoutWait(ShoutoutBlock Block, TimeSpan Left)
{
    public bool Allowed => Block == ShoutoutBlock.None;
}

public sealed class ShoutoutLimits
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SameUserGap = TimeSpan.FromHours(1);

    private readonly Dictionary<string, DateTime> _byUser = new(StringComparer.Ordinal);
    private DateTime? _last;

    public ShoutoutWait Check(string userId, DateTime nowUtc)
    {
        var recent = _last is { } last && nowUtc - last < Gap ? Gap - (nowUtc - last) : TimeSpan.Zero;
        var same = _byUser.TryGetValue(userId, out var at) && nowUtc - at < SameUserGap ? SameUserGap - (nowUtc - at) : TimeSpan.Zero;
        if (same > TimeSpan.Zero && same >= recent) return new ShoutoutWait(ShoutoutBlock.SameUser, same);
        if (recent > TimeSpan.Zero) return new ShoutoutWait(ShoutoutBlock.Recent, recent);
        return new ShoutoutWait(ShoutoutBlock.None, TimeSpan.Zero);
    }

    public void Sent(string userId, DateTime nowUtc)
    {
        _last = nowUtc;
        foreach (var old in _byUser.Where(pair => nowUtc - pair.Value >= SameUserGap).Select(pair => pair.Key).ToList()) _byUser.Remove(old);
        _byUser[userId] = nowUtc;
    }

    public static string MenuText(ShoutoutWait wait) => wait.Block switch
    {
        ShoutoutBlock.Recent => $"Отметить (можно через {Clock(wait.Left)})",
        ShoutoutBlock.SameUser when wait.Left >= TimeSpan.FromMinutes(1) => $"Отметить (этого снова через {(int)Math.Ceiling(wait.Left.TotalMinutes)} мин)",
        ShoutoutBlock.SameUser => $"Отметить (этого снова через {Clock(wait.Left)})",
        _ => "Отметить",
    };

    private static string Clock(TimeSpan left)
    {
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
