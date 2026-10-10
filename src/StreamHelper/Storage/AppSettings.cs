using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamHelper.Storage;

internal static class Secrets
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}

public sealed class AppSettings
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    public string ClientId { get; set; } = "";
    public string ClientSecretProtected { get; set; } = "";
    public string AccessTokenProtected { get; set; } = "";
    public string RefreshTokenProtected { get; set; } = "";
    public DateTime AccessTokenExpiresUtc { get; set; }

    public int RedirectPort { get; set; } = 7653;

    public Dictionary<string, HotkeyBinding> Hotkeys { get; set; } = new();

    public HotkeyBinding GetHotkey(HotkeyAction action) =>
        Hotkeys != null && Hotkeys.TryGetValue(action.ToString(), out var binding) && binding != null ? binding : new HotkeyBinding();

    public void SetHotkey(HotkeyAction action, uint modifiers, uint virtualKey)
    {
        Hotkeys ??= new Dictionary<string, HotkeyBinding>();
        if (virtualKey == 0) Hotkeys.Remove(action.ToString());
        else Hotkeys[action.ToString()] = new HotkeyBinding { Modifiers = modifiers, VirtualKey = virtualKey };
    }

    public HotkeyAction? FindHotkeyOwner(uint modifiers, uint virtualKey, HotkeyAction except)
    {
        foreach (var action in HotkeyActions.All)
        {
            if (action == except) continue;
            var binding = GetHotkey(action);
            if (binding.IsSet && binding.VirtualKey == virtualKey && binding.Modifiers == modifiers) return action;
        }
        return null;
    }

    public long LastDonationId { get; set; }
    public bool Baselined { get; set; }

    public string DonatePayKeyProtected { get; set; } = "";
    public bool DonatePayBaselined { get; set; }
    public List<string> DonatePaySeen { get; set; } = new();
    public string DonateXTokenProtected { get; set; } = "";
    public bool DonateXBaselined { get; set; }
    public List<string> DonateXSeen { get; set; } = new();

    public string TwitchClientId { get; set; } = "";
    public string TwitchAccessTokenProtected { get; set; } = "";
    public string TwitchRefreshTokenProtected { get; set; } = "";
    public DateTime TwitchAccessTokenExpiresUtc { get; set; }
    public string TwitchUserId { get; set; } = "";
    public string TwitchLogin { get; set; } = "";
    public string TwitchScopes { get; set; } = "";
    public string TwitchChannelId { get; set; } = "";
    public string TwitchChannelLogin { get; set; } = "";
    public string TwitchChannelName { get; set; } = "";

    [JsonIgnore] public bool IsOwnChannel => TwitchChannelId.Length == 0 || TwitchChannelId == TwitchUserId;

    [JsonIgnore] public string ChannelId => IsOwnChannel ? TwitchUserId : TwitchChannelId;

    [JsonIgnore] public string ChannelLabel => IsOwnChannel ? TwitchLogin : TwitchChannelName.Length > 0 ? TwitchChannelName : TwitchChannelLogin;

    [JsonIgnore]
    public string ConnectedText => IsOwnChannel ? $"Подключено · {TwitchLogin}" : $"Подключено · {TwitchLogin} · канал {ChannelLabel}";

    public bool SelectChannel(string id, string login, string name)
    {
        if (id == TwitchUserId) id = login = name = "";
        if (id == TwitchChannelId)
        {
            TwitchChannelLogin = login;
            TwitchChannelName = name;
            return false;
        }
        TwitchChannelId = id;
        TwitchChannelLogin = login;
        TwitchChannelName = name;
        FollowersBaselined = false;
        LastFollowerAtUtc = default;
        LastFollowerUserIds = new List<string>();
        return true;
    }

    public bool FollowersBaselined { get; set; }
    public DateTime LastFollowerAtUtc { get; set; }
    public List<string> LastFollowerUserIds { get; set; } = new();

    public bool ShowToast { get; set; } = true;

    public int ToastSeconds { get; set; } = 6;
    public bool NotifyFollowers { get; set; } = true;
    public bool NotifySubscribers { get; set; } = true;
    public bool NotifyRewards { get; set; } = true;
    public bool NotifyPings { get; set; } = true;
    public bool NotifyRaids { get; set; } = true;
    public bool NotifyStreaks { get; set; } = true;

    public static readonly string[] DefaultPingIgnored =
    {
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "soundalerts", "sery_bot", "commanderroot",
    };

    public List<string> PingIgnoredChatters { get; set; } = new(DefaultPingIgnored);

    public List<string> PingWords { get; set; } = new();

    public bool ChatHidden { get; set; }

    public Dictionary<string, string> ChatHighlights { get; set; } = new();
    public double ChatOpacity { get; set; } = 0.6;

    public double ChatCellOpacity { get; set; } = 0.2;

    public double ChatOutlineOpacity { get; set; } = 0.9;
    public double ChatFontSize { get; set; } = 14;

    public int ChatMuteMinutes { get; set; } = 10;
    public double? ChatLeft { get; set; }
    public double? ChatTop { get; set; }
    public double? ChatWidth { get; set; }
    public double? ChatHeight { get; set; }

    public Dictionary<string, RewardSound> RewardSounds { get; set; } = new();

    public List<string> SoundRewardIds { get; set; } = new();

    public bool RewardSoundsMuted { get; set; }

    public bool MuteSwitchesRewardsOff { get; set; } = true;

    public List<string> MuteSwitchedOffRewardIds { get; set; } = new();

    public bool MuteBadgeHidden { get; set; }

    public bool CreditsCollapsed { get; set; }
    public double MuteBadgeScale { get; set; } = 1.0;
    public double? MuteBadgeLeft { get; set; }
    public double? MuteBadgeTop { get; set; }

    public string RewardIconsFolder { get; set; } = "";

    public string ProfileFolder { get; set; } = "";

    public string UpdateSkipVersion { get; set; } = "";

    public int PollSeconds { get; set; } = 60;

    public int PredictionSeconds { get; set; } = 120;

    public bool PollPointsVoting { get; set; }

    public int PollPointsPerVote { get; set; } = 100;

    public double ToastOpacity { get; set; } = 1.0;
    public double ToastScale { get; set; } = 1.0;
    public double? ToastLeft { get; set; }
    public double? ToastTop { get; set; }

    public bool OnlyOwnRewards { get; set; }

    public List<string> ManagedRewardIds { get; set; } = new();
    public Dictionary<string, string> RewardCopies { get; set; } = new();

    public List<string> HiddenRewardIds { get; set; } = new();

    public bool IsRewardHidden(string rewardId) => HiddenRewardIds.Contains(rewardId);

    public bool AllowsReward(string rewardId) => !OnlyOwnRewards || ManagedRewardIds.Contains(rewardId);

    public bool ListsReward(string rewardId) => AllowsReward(rewardId) && !IsRewardHidden(rewardId);
    public int SelectedTab { get; set; }
    public int SettingsSection { get; set; }

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    [JsonIgnore]
    public string DonatePayKey
    {
        get => Secrets.Unprotect(DonatePayKeyProtected);
        set => DonatePayKeyProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string DonateXToken
    {
        get => Secrets.Unprotect(DonateXTokenProtected);
        set => DonateXTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore] public bool HasDonatePayKey => DonatePayKey.Length > 0;

    [JsonIgnore] public bool HasDonateXToken => DonateXToken.Length > 0;

    [JsonIgnore] public bool HasAnyDonationSource => HasTokens || HasDonatePayKey || HasDonateXToken;

    [JsonIgnore]
    public string ClientSecret
    {
        get => Secrets.Unprotect(ClientSecretProtected);
        set => ClientSecretProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string AccessToken
    {
        get => Secrets.Unprotect(AccessTokenProtected);
        set => AccessTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string RefreshToken
    {
        get => Secrets.Unprotect(RefreshTokenProtected);
        set => RefreshTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string TwitchAccessToken
    {
        get => Secrets.Unprotect(TwitchAccessTokenProtected);
        set => TwitchAccessTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string TwitchRefreshToken
    {
        get => Secrets.Unprotect(TwitchRefreshTokenProtected);
        set => TwitchRefreshTokenProtected = Secrets.Protect(value);
    }

    [JsonIgnore]
    public string EffectiveTwitchClientId =>
        string.IsNullOrWhiteSpace(TwitchClientId) ? BuiltInCredentials.TwitchClientId : TwitchClientId.Trim();

    [JsonIgnore]
    public bool HasTwitchCredentials => !string.IsNullOrWhiteSpace(EffectiveTwitchClientId);

    [JsonIgnore]
    public bool HasTwitchTokens =>
        !string.IsNullOrEmpty(TwitchRefreshTokenProtected) && !string.IsNullOrEmpty(TwitchUserId);

    public bool HasScope(string scope) =>
        TwitchScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope);

    [JsonIgnore]
    public bool HasSubscriptionScope => HasScope("channel:read:subscriptions");

    [JsonIgnore]
    public bool HasRedemptionScope => HasScope("channel:read:redemptions");

    [JsonIgnore]
    public bool HasManageScope => HasScope("channel:manage:redemptions");

    [JsonIgnore]
    public bool HasChatScope => HasScope("user:read:chat");

    [JsonIgnore]
    public bool HasModerationScope => HasScope("moderator:manage:banned_users");

    [JsonIgnore]
    public bool HasPollScope => HasScope("channel:manage:polls");

    [JsonIgnore]
    public bool HasPredictionScope => HasScope("channel:manage:predictions");

    [JsonIgnore]
    public bool HasShoutoutScope => HasScope("moderator:manage:shoutouts");

    [JsonIgnore]
    public bool HasModeratedChannelsScope => HasScope("user:read:moderated_channels");

    [JsonIgnore]
    public string RedirectUri => $"http://127.0.0.1:{RedirectPort}/callback";

    [JsonIgnore]
    public string EffectiveClientId =>
        string.IsNullOrWhiteSpace(ClientId) ? BuiltInCredentials.DonationAlertsClientId : ClientId.Trim();

    [JsonIgnore]
    public bool UsesCodeFlow => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrEmpty(ClientSecretProtected);

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrWhiteSpace(EffectiveClientId);

    [JsonIgnore]
    public bool HasTokens => !string.IsNullOrEmpty(AccessTokenProtected) || !string.IsNullOrEmpty(RefreshTokenProtected);
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public AppSettings Current { get; }

    public string ToJson()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(Current, Json);
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, _path, true);
            }
            catch (Exception ex)
            {
                Log.Write("Settings save failed: " + ex.Message);
            }
        }
    }

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
        }
        return new AppSettings();
    }
}
