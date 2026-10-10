using System;
using System.Globalization;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class VersionRow
{
    public VersionRow(UpdateRelease release, Version current)
    {
        Release = release;
        IsCurrent = release.Version == current;
        IsOlder = release.Version < current;
    }

    public UpdateRelease Release { get; }
    public bool IsCurrent { get; }
    public bool IsOlder { get; }
    public string Title => Release.Version.ToString(4);
    public string Mark => IsCurrent ? "установлена" : IsOlder ? "старее" : "новее";

    public string DateText =>
        Release.PublishedUtc is { } at ? at.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";

    public string InstallText(bool armed) =>
        IsCurrent ? "Уже установлена" : armed ? $"Да, установить {Title}" : $"Установить {Title}";

    public static string OlderWarning =>
        "Это более старая версия: того, что появилось позже, в ней не будет. Настройки и списки останутся, " +
        "перед установкой сохранится их резервная копия. Нажми ещё раз, чтобы установить.";

    public static string Hint =>
        $"Можно поставить любую вышедшую версию, начиная с {AppUpdate.OldestChoosable.ToString(4)}. " +
        "Если поставить более старую, напоминаний о новой не будет, пока не выйдет ещё более новая.";
}