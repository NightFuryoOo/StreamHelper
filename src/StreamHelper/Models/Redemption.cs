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
    private string? _videoUrl;
    private string? _videoTitle;
    private string? _videoThumbnail;
    private bool _videoIsClip;
    public string Key { get; set; } = "";
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RewardId { get; set; } = "";
    public string RewardTitle { get; set; } = "";
    public long Cost { get; set; }
    public string UserInput { get; set; } = "";
    public DateTime AtUtc { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoUrl
    {
        get => _videoUrl;
        set
        {
            if (_videoUrl == value) return;
            _videoUrl = value;
            Raise();
            Raise(nameof(HasVideo));
            Raise(nameof(VideoName));
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoTitle
    {
        get => _videoTitle;
        set
        {
            if (_videoTitle == value) return;
            _videoTitle = value;
            Raise();
            Raise(nameof(VideoName));
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VideoThumbnail
    {
        get => _videoThumbnail;
        set
        {
            if (_videoThumbnail == value) return;
            _videoThumbnail = value;
            Raise();
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool VideoIsClip
    {
        get => _videoIsClip;
        set
        {
            if (_videoIsClip == value) return;
            _videoIsClip = value;
            Raise();
            Raise(nameof(VideoLabel));
            Raise(nameof(VideoName));
        }
    }

    [JsonIgnore] public bool HasVideo => !string.IsNullOrEmpty(_videoUrl);
    [JsonIgnore] public string VideoLabel => _videoIsClip ? "Клип Twitch" : "Видео YouTube";
    [JsonIgnore] public string VideoName => (_videoIsClip ? "Клип: " : "Видео: ") + (_videoTitle ?? _videoUrl ?? "");
    [JsonIgnore] public bool HasVideoTitle => HasVideo && !string.IsNullOrEmpty(_videoTitle) && _videoTitle != _videoUrl;

    public void AttachVideo(string url, string title, string thumbnail, bool clip)
    {
        VideoIsClip = clip;
        VideoThumbnail = thumbnail.Length > 0 ? thumbnail : null;
        VideoTitle = title.Length > 0 ? title : url;
        VideoUrl = url;
    }

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

    public static string FormatCost(long cost) => $"{cost.ToString("N0", Spaced)} {Words.Plural(cost, "балл", "балла", "баллов")}";
}
