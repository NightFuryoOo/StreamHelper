using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public static class ChatRules
{
    public static bool CanModerate(ChatMessage message, AppSettings settings) =>
        !settings.IsViewerChannel && message.ChatterId.Length > 0 && message.ChatterId != settings.TwitchUserId && message.ChatterId != settings.ChannelId;

    public static string MenuText(string text) => text.Replace("_", "__");

    public static string ChannelHeader(AppSettings settings) =>
        settings.IsOwnChannel ? "Канал" : $"Канал · {settings.ChannelLabel}" + (settings.IsViewerChannel ? " (зритель)" : "");

    public static string? ChannelLogin(string? text)
    {
        var login = (text ?? "").Trim();
        var site = login.IndexOf("twitch.tv/", StringComparison.OrdinalIgnoreCase);
        if (site >= 0) login = login[(site + "twitch.tv/".Length)..];
        var end = login.IndexOfAny(new[] { '/', '?', '#' });
        if (end >= 0) login = login[..end];
        login = login.TrimStart('@').Trim();
        return Regex.IsMatch(login, "^[A-Za-z0-9_]{3,25}$") ? login.ToLowerInvariant() : null;
    }

    public static (string Text, bool Warn) ChannelStatus(AppSettings settings, IReadOnlyList<ModeratedChannel>? moderated, bool loading, string? error)
    {
        if (!settings.HasModeratedChannelsScope) return ("Чтобы выбрать канал, где ты модератор, переподключи Twitch: нужно новое право.", true);
        if (loading) return ("Загружаю каналы, где ты модератор…", false);
        if (error != null) return ("Не удалось получить список каналов: " + error, true);
        if (moderated == null) return ("", false);
        if (!settings.IsOwnChannel && !settings.IsViewerChannel && moderated.All(c => c.Id != settings.TwitchChannelId))
        {
            return ($"Twitch не считает тебя модератором канала {settings.ChannelLabel}: бан, мут и «Отметить» там не сработают.", true);
        }
        if (moderated.Count == 0) return ("Ты пока не модератор ни одного канала.", false);
        return ("", false);
    }
}