using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StreamHelper.Storage;

namespace StreamHelper.Models;

public enum SubscriptionKind
{
    New,
    Resub,
    Gift,
}

public sealed class Subscriber : ISeenItem, ISelectable
{
    private bool _seen;
    private bool _selected;

    public string Key { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SubscriptionKind Kind { get; set; }

    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Tier { get; set; } = "1000";
    public int Months { get; set; }
    public int StreakMonths { get; set; }
    public string Message { get; set; } = "";
    public int GiftTotal { get; set; }
    public bool IsAnonymous { get; set; }
    public DateTime AtUtc { get; set; }

    public bool Seen
    {
        get => _seen;
        set
        {
            if (_seen == value) return;
            _seen = value;
            Raise();
            Raise(nameof(IsNew));
        }
    }

    [JsonIgnore]
    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Raise();
        }
    }

    [JsonIgnore] public bool CanSelect => true;
    [JsonIgnore] public bool IsNew => !_seen;
    [JsonIgnore] public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    [JsonIgnore]
    public string Name =>
        IsAnonymous ? "Аноним" : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : !string.IsNullOrWhiteSpace(Login) ? Login : "Без имени";

    [JsonIgnore]
    public string TierText => Tier switch
    {
        "1000" => "Tier 1",
        "2000" => "Tier 2",
        "3000" => "Tier 3",
        "" => "",
        var other => other,
    };

    [JsonIgnore]
    public string KindText => Kind switch
    {
        SubscriptionKind.Resub => "продление",
        SubscriptionKind.Gift => "подарил подписки",
        _ => "новая подписка",
    };

    [JsonIgnore]
    public string ToastTitle => Kind switch
    {
        SubscriptionKind.Resub => "Продление подписки",
        SubscriptionKind.Gift => "Подарочные подписки",
        _ => "Новая подписка",
    };

    [JsonIgnore]
    public string DetailText => Kind switch
    {
        SubscriptionKind.Gift => $"{TierText} · {GiftTotal} шт.".Trim(' ', '·'),
        SubscriptionKind.Resub => Months > 0
            ? (StreakMonths > 1 ? $"{TierText} · {Months} мес., подряд {StreakMonths}" : $"{TierText} · {Months} мес.")
            : TierText,
        _ => TierText,
    };

    [JsonIgnore]
    public string TimeText =>
        AtUtc == default ? "" : DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public string SubText => string.IsNullOrEmpty(DetailText) ? TimeText : $"{TimeText} · {DetailText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
