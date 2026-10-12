using System;
using System.Globalization;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Ui;

public static class ChannelLive
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Viewers(int count) => Count(count) + " " + Words.Plural(count, "зритель", "зрителя", "зрителей");

    public static string Count(int count) => count.ToString("N0", Russian);

    public static string Change(int delta) => delta == 0 ? "" : (delta > 0 ? "+" : "−") + Count(Math.Abs(delta));

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