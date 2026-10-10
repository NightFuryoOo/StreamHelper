using System.Text.Json;
using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StreamHelper.Storage;

namespace StreamHelper.Models;

public enum RedemptionStatus
{
    Pending,
    Accepted,
    Rejected,
    Processed,
}

public sealed class Redemption : ISeenItem, ISelectable
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly NumberFormatInfo Spaced = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    private bool _seen;
    private bool _done;
    private RedemptionStatus _status;
    private bool _canManage;
    private bool _confirmingReject;
    private bool _isBusy;
    private bool _selected;
    public string Key { get; set; } = "";
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RewardId { get; set; } = "";
    public string RewardTitle { get; set; } = "";
    public long Cost { get; set; }
    public string UserInput { get; set; } = "";
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

    public bool Done
    {
        get => _done;
        set
        {
            if (_done == value) return;
            _done = value;
            Raise();
            if (value) Seen = true;
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RedemptionStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            RaiseActionState();
        }
    }

    public bool CanManage
    {
        get => _canManage;
        set
        {
            if (_canManage == value) return;
            _canManage = value;
            Raise();
            RaiseActionState();
        }
    }

    [JsonIgnore]
    public bool ConfirmingReject
    {
        get => _confirmingReject;
        set
        {
            if (_confirmingReject == value) return;
            _confirmingReject = value;
            Raise();
            RaiseActionState();
        }
    }

    [JsonIgnore]
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            Raise();
            Raise(nameof(CanPress));
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

    [JsonIgnore] public bool CanSelect => _canManage && _status == RedemptionStatus.Pending;

    [JsonIgnore] public bool ShowActions => _canManage && _status == RedemptionStatus.Pending && !_confirmingReject;
    [JsonIgnore] public bool ShowConfirm => _canManage && _status == RedemptionStatus.Pending && _confirmingReject;
    [JsonIgnore] public bool ShowDelete => !(_canManage && _status == RedemptionStatus.Pending);
    [JsonIgnore] public bool CanPress => !_isBusy;
    [JsonIgnore] public bool HasStatusText => StatusText.Length > 0;

    [JsonIgnore]
    public string StatusText => _status switch
    {
        RedemptionStatus.Accepted => "Принято в Twitch",
        RedemptionStatus.Rejected => "Отклонено · баллы возвращены зрителю",
        RedemptionStatus.Processed => "Уже обработано в Twitch",
        _ => "",
    };

    private void RaiseActionState()
    {
        Raise(nameof(ShowActions));
        Raise(nameof(ShowConfirm));
        Raise(nameof(ShowDelete));
        Raise(nameof(CanSelect));
        if (!CanSelect) Selected = false;
        Raise(nameof(StatusText));
        Raise(nameof(HasStatusText));
    }

    [JsonIgnore] public bool IsNew => !_seen;
    [JsonIgnore] public bool HasInput => !string.IsNullOrWhiteSpace(UserInput);

    [JsonIgnore]
    public string Name =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : !string.IsNullOrWhiteSpace(Login) ? Login : "Без имени";

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(RewardTitle) ? "Награда" : RewardTitle;

    [JsonIgnore] public string CostText => FormatCost(Cost);

    [JsonIgnore]
    public string TimeText =>
        AtUtc == default ? "" : DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatCost(long cost)
    {
        var mod100 = Math.Abs(cost) % 100;
        var mod10 = mod100 % 10;
        var word = mod100 is >= 11 and <= 14 ? "баллов" : mod10 == 1 ? "балл" : mod10 is >= 2 and <= 4 ? "балла" : "баллов";
        return $"{cost.ToString("N0", Spaced)} {word}";
    }
}
