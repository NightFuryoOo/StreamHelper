using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed record ProfileNotice(string Text, string? BackupPath);

public static class ProfileTexts
{
    public static string DefaultFileName(DateTime now) =>
        "StreamHelper профиль " + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Profile.Extension;

    public static bool OtherChannel(ProfileInfo info, string currentTwitchUserId) =>
        info.TwitchUserId.Length > 0 && currentTwitchUserId.Length > 0 &&
        !string.Equals(info.TwitchUserId, currentTwitchUserId, StringComparison.Ordinal);

    public static string Confirm(ProfileInfo info, string currentTwitchUserId)
    {
        var head = "Загрузить профиль от " + info.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        if (info.TwitchLogin.Length > 0) head += ", канал " + info.TwitchLogin;
        if (info.HasLists) head += ", со списками";
        var lines = new List<string>
        {
            head + "?",
            "Текущие настройки будут заменены, программа перезапустится. Подключения к сервисам останутся как есть, прежние настройки сохранятся в резервную копию.",
        };
        if (OtherChannel(info, currentTwitchUserId))
        {
            lines.Add("Профиль сохранён с другого канала Twitch: настройки наград к подключённому каналу не подойдут.");
        }
        return string.Join("\n", lines);
    }

    public static string Saved(string path, ProfileResult result)
    {
        if (!result.Success) return "Не получилось сохранить профиль: " + result.Error;
        var text = "Профиль сохранён" + (result.Info?.HasLists == true ? " со списками" : "") + ": " + path;
        if (result.MissingSounds > 0) text += $"\nНе найдено файлов звуков: {result.MissingSounds}, у этих наград звук не перенесётся.";
        return text;
    }

    public static ProfileNotice Imported(ProfileResult result, AppSettings current)
    {
        if (!result.Success) return new ProfileNotice(result.Error, result.BackupPath);

        var info = result.Info ?? new ProfileInfo();
        var lines = new List<string> { "Профиль загружен. Прежние настройки сохранены в резервную копию." };

        var connected = Profile.ConnectedServices(current);
        var missing = info.Connected.Where(service => !connected.Contains(service)).ToList();
        if (missing.Count > 0)
        {
            var names = missing.Select(service =>
                service == Profile.Twitch && info.TwitchLogin.Length > 0 ? $"{Profile.Twitch} (канал {info.TwitchLogin})" : service);
            lines.Add("Подключи заново: " + string.Join(", ", names) + ".");
        }
        if (OtherChannel(info, current.TwitchUserId))
        {
            lines.Add($"Профиль сохранён с канала {info.TwitchLogin}, а подключён другой: настройки наград к нему не подойдут.");
        }
        return new ProfileNotice(string.Join("\n", lines), result.BackupPath);
    }
}
