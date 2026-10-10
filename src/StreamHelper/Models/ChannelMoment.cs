using System.Text.Json;
using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StreamHelper.Storage;

namespace StreamHelper.Models;

public enum MomentKind
{
    Raid,
    Streak,
}

public sealed class ChannelMoment : ISeenItem, ISelectable
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private bool _seen;
    private bool _selected;

    public string Key { get; set; } = "";
    public MomentKind Kind { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Viewers { get; set; }
    public int StreakCount { get; set; }
    public int ChannelPoints { get; set; }
    public string Message { get; set; } = "";
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
    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
    [JsonIgnore] public bool IsNew => !_seen;
    [JsonIgnore] public bool IsRaid => Kind == MomentKind.Raid;
    [JsonIgnore] public string KindText => IsRaid ? "рейд" : "стрик";
    [JsonIgnore] public bool HasMessage => Message.Length > 0;

    [JsonIgnore]
    public string Detail => IsRaid
        ? $"привёл {Viewers} {Words.Plural(Viewers, "зрителя", "зрителей", "зрителей")}"
        : $"серия просмотров: {StreakCount} {Words.Plural(StreakCount, "стрим", "стрима", "стримов")}" +
          (ChannelPoints > 0 ? $" · +{Redemption.FormatCost(ChannelPoints)}" : "");

    [JsonIgnore]
    public string TimeText =>
        AtUtc == default ? "" : DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
