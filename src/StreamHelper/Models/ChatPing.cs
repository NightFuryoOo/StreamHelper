using System.Text.Json;
using System.Collections.Generic;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using StreamHelper.Storage;

namespace StreamHelper.Models;

public sealed class ChatPing : ISeenItem, ISelectable
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    private bool _seen;
    private bool _selected;

    public string Key { get; set; } = "";
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
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

    [JsonIgnore] public bool IsNew => !_seen;
    [JsonIgnore] public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    [JsonIgnore]
    public string Name =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : !string.IsNullOrWhiteSpace(Login) ? Login : "Без имени";

    [JsonIgnore]
    public string TimeText =>
        AtUtc == default ? "" : DateTime.SpecifyKind(AtUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}