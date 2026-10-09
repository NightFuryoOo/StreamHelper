using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StreamHelper.Storage;

namespace StreamHelper.Models;

public sealed class Follower : ISeenItem, ISelectable
{
    private bool _seen;
    private bool _selected;

    public string UserId { get; set; } = "";
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime FollowedAtUtc { get; set; }

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

    [JsonIgnore] public string Key => MakeKey(UserId, FollowedAtUtc);
    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Login : DisplayName;
    [JsonIgnore] public bool IsNew => !_seen;

    [JsonIgnore]
    public string TimeText =>
        FollowedAtUtc == default ? "" : DateTime.SpecifyKind(FollowedAtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string MakeKey(string userId, DateTime followedAtUtc) =>
        userId + "|" + DateTime.SpecifyKind(followedAtUtc, DateTimeKind.Utc).Ticks.ToString(CultureInfo.InvariantCulture);
}
