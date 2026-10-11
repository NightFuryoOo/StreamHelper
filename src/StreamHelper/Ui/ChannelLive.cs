using System;
using System.Globalization;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public static class ChannelLive
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Viewers(int count)
    {
        var tens = count % 100;
        var ones = count % 10;
        var word = tens is >= 11 and <= 14 ? "зрителей" : ones == 1 ? "зритель" : ones is >= 2 and <= 4 ? "зрителя" : "зрителей";
        return count.ToString("N0", Russian) + " " + word;
    }

    public static string Duration(TimeSpan length)
    {
        if (length < TimeSpan.FromMinutes(1)) return "только начался";
        var hours = (int)length.TotalHours;
        var minutes = length.Minutes;
        if (hours == 0) return $"идёт {minutes} мин";
        return minutes == 0 ? $"идёт {hours} ч" : $"идёт {hours} ч {minutes} мин";
    }

    public static string Describe(LiveStream stream, DateTime nowUtc)
    {
        var line = "В эфире";
        if (stream.Game.Length > 0) line += " · " + stream.Game;
        line += " · " + Viewers(stream.Viewers);
        if (stream.StartedUtc is { } started) line += " · " + Duration(nowUtc - started);
        return stream.Title.Trim().Length > 0 ? line + "\n" + stream.Title.Trim() : line;
    }
}