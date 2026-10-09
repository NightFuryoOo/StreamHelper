using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace StreamHelper.Models;

public static class DonationSources
{
    public const string DonationAlerts = "DonationAlerts";
    public const string DonatePay = "DonatePay";
    public const string DonateX = "DonateX";
}

public sealed class Donation : StreamHelper.Storage.ISeenItem, StreamHelper.Storage.ISelectable
{
    private bool _seen;
    private bool _selected;
    private bool _done;
    private string? _customName;
    private bool _isEditing;
    private string _editText = "";

    public long Id { get; set; }

    public string Source { get; set; } = "";

    public string ExternalId { get; set; } = "";
    public string Username { get; set; } = "";
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }

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

    public string? CustomName
    {
        get => _customName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_customName == normalized) return;
            _customName = normalized;
            Raise();
            Raise(nameof(DisplayName));
            Raise(nameof(IsRenamed));
            Raise(nameof(SubText));
        }
    }

    [JsonIgnore] public string DisplayName => _customName ?? (string.IsNullOrWhiteSpace(Username) ? "Без имени" : Username);
    [JsonIgnore] public bool IsRenamed => _customName != null;

    [JsonIgnore] public string SourceName => string.IsNullOrEmpty(Source) ? DonationSources.DonationAlerts : Source;

    [JsonIgnore]
    public string Key => string.IsNullOrEmpty(Source)
        ? Id.ToString(CultureInfo.InvariantCulture)
        : Source + ":" + (ExternalId.Length > 0 ? ExternalId : Id.ToString(CultureInfo.InvariantCulture));

    [JsonIgnore]
    public string SubText =>
        IsRenamed && !string.IsNullOrWhiteSpace(Username) ? $"{TimeText} · в {SourceName}: {Username}"
        : string.IsNullOrEmpty(Source) ? TimeText
        : $"{TimeText} · {Source}";

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise();
        }
    }

    [JsonIgnore]
    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value;
            Raise();
        }
    }

    public void Rename(string? text)
    {
        var trimmed = text?.Trim();
        CustomName = string.Equals(trimmed, Username, StringComparison.Ordinal) ? null : trimmed;
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
    [JsonIgnore] public string AmountText => FormatAmount(Amount, Currency);

    [JsonIgnore]
    public string TimeText =>
        CreatedAtUtc == default ? "" : DateTime.SpecifyKind(CreatedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatAmount(decimal amount, string currency)
    {
        var number = amount.ToString("0.##", CultureInfo.InvariantCulture);
        var symbol = currency.ToUpperInvariant() switch
        {
            "RUB" => "₽",
            "USD" => "$",
            "EUR" => "€",
            "UAH" => "₴",
            "KZT" => "₸",
            "BYN" => "Br",
            "" => "",
            var other => other,
        };
        return symbol.Length == 0 ? number : $"{number} {symbol}";
    }

    [JsonIgnore]
    public string Summary => HasMessage ? $"{DisplayName} · {AmountText} · {Message}" : $"{DisplayName} · {AmountText}";
}
