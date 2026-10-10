using System;
using System.Text.Json.Serialization;

namespace StreamHelper.Storage;

public enum HotkeyAction
{
    Donations,
    Followers,
    Subscribers,
    Rewards,
    Settings,
    HideToasts,
    Pings,
    ChatInteract,
    ChatToggle,
    RewardSoundsMute,
    Moments,
}

public sealed class HotkeyBinding
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    [JsonIgnore]
    public bool IsSet => VirtualKey != 0;
}

public static class HotkeyActions
{
    public static readonly HotkeyAction[] All = Enum.GetValues<HotkeyAction>();

    public static string Title(HotkeyAction action) => action switch
    {
        HotkeyAction.Donations => "Донаты",
        HotkeyAction.Followers => "Фолловеры",
        HotkeyAction.Subscribers => "Подписки",
        HotkeyAction.Rewards => "Награды",
        HotkeyAction.Settings => "Настройки",
        HotkeyAction.HideToasts => "Скрыть уведомления",
        HotkeyAction.Pings => "Пинги",
        HotkeyAction.ChatInteract => "Чат: управление мышью",
        HotkeyAction.ChatToggle => "Чат: показать / скрыть",
        HotkeyAction.RewardSoundsMute => "Звуки наград: выкл / вкл",
        HotkeyAction.Moments => "Рейды и стрики",
        _ => action.ToString(),
    };

    public static int TabIndex(HotkeyAction action) => action switch
    {
        HotkeyAction.Donations => 0,
        HotkeyAction.Followers => 1,
        HotkeyAction.Subscribers => 2,
        HotkeyAction.Rewards => 3,
        HotkeyAction.Pings => 4,
        HotkeyAction.Moments => 5,
        _ => -1,
    };
}
