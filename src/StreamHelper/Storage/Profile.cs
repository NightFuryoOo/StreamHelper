using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamHelper.Storage;

public sealed class ProfileInfo
{
    public string App { get; set; } = "";
    public int Format { get; set; }
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string TwitchUserId { get; set; } = "";
    public string TwitchLogin { get; set; } = "";
    public List<string> Connected { get; set; } = new();
    public bool HasLists { get; set; }
    public int Sounds { get; set; }
}

public sealed record ProfileResult(bool Success, string Error, ProfileInfo? Info = null, string? BackupPath = null, int MissingSounds = 0);

public static class Profile
{
    public const int Format = 1;
    public const string Extension = ".shprofile";
    public const string AppName = "StreamHelper";
    public const string BackupsFolderName = "backups";
    public const int KeptBackups = 10;

    public const string Twitch = "Twitch";
    public const string DonationAlerts = "DonationAlerts";
    public const string DonatePay = "DonatePay";
    public const string DonateX = "DonateX";

    private const string PendingFileName = "import" + Extension;
    private const string InfoEntry = "profile.json";
    private const string SettingsEntry = "settings.json";
    private const string SoundsFolder = "alerts";
    private const string SoundsPrefix = SoundsFolder + "/";
    private const string ListsPrefix = "lists/";
    private const long MaxSmallEntry = 4L * 1024 * 1024;
    private const long MaxListEntry = 64L * 1024 * 1024;
    private const string NotAProfile = "Это не файл профиля StreamHelper или он повреждён.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static readonly string[] ListFiles = { "donations.json", "followers.json", "subscribers.json", "redemptions.json", "pings.json" };

    public static readonly string[] LocalSettings =
    {
        nameof(AppSettings.ClientId),
        nameof(AppSettings.ClientSecretProtected),
        nameof(AppSettings.AccessTokenProtected),
        nameof(AppSettings.RefreshTokenProtected),
        nameof(AppSettings.AccessTokenExpiresUtc),
        nameof(AppSettings.RedirectPort),
        nameof(AppSettings.LastDonationId),
        nameof(AppSettings.Baselined),
        nameof(AppSettings.DonatePayKeyProtected),
        nameof(AppSettings.DonatePayBaselined),
        nameof(AppSettings.DonatePaySeen),
        nameof(AppSettings.DonateXTokenProtected),
        nameof(AppSettings.DonateXBaselined),
        nameof(AppSettings.DonateXSeen),
        nameof(AppSettings.TwitchClientId),
        nameof(AppSettings.TwitchAccessTokenProtected),
        nameof(AppSettings.TwitchRefreshTokenProtected),
        nameof(AppSettings.TwitchAccessTokenExpiresUtc),
        nameof(AppSettings.TwitchUserId),
        nameof(AppSettings.TwitchLogin),
        nameof(AppSettings.TwitchScopes),
        nameof(AppSettings.FollowersBaselined),
        nameof(AppSettings.LastFollowerAtUtc),
        nameof(AppSettings.LastFollowerUserIds),
        nameof(AppSettings.RewardIconsFolder),
        nameof(AppSettings.ProfileFolder),
    };

    public static readonly string[] SharedSettings =
    {
        nameof(AppSettings.Hotkeys),
        nameof(AppSettings.ShowToast),
        nameof(AppSettings.ToastSeconds),
        nameof(AppSettings.NotifyFollowers),
        nameof(AppSettings.NotifySubscribers),
        nameof(AppSettings.NotifyRewards),
        nameof(AppSettings.NotifyPings),
        nameof(AppSettings.PingIgnoredChatters),
        nameof(AppSettings.PingWords),
        nameof(AppSettings.ChatHidden),
        nameof(AppSettings.ChatHighlights),
        nameof(AppSettings.ChatOpacity),
        nameof(AppSettings.ChatCellOpacity),
        nameof(AppSettings.ChatOutlineOpacity),
        nameof(AppSettings.ChatFontSize),
        nameof(AppSettings.ChatMuteMinutes),
        nameof(AppSettings.ChatLeft),
        nameof(AppSettings.ChatTop),
        nameof(AppSettings.ChatWidth),
        nameof(AppSettings.ChatHeight),
        nameof(AppSettings.RewardSounds),
        nameof(AppSettings.SoundRewardIds),
        nameof(AppSettings.RewardSoundsMuted),
        nameof(AppSettings.MuteBadgeHidden),
        nameof(AppSettings.CreditsCollapsed),
        nameof(AppSettings.MuteBadgeScale),
        nameof(AppSettings.MuteBadgeLeft),
        nameof(AppSettings.MuteBadgeTop),
        nameof(AppSettings.ToastOpacity),
        nameof(AppSettings.ToastScale),
        nameof(AppSettings.ToastLeft),
        nameof(AppSettings.ToastTop),
        nameof(AppSettings.OnlyOwnRewards),
        nameof(AppSettings.ManagedRewardIds),
        nameof(AppSettings.RewardCopies),
        nameof(AppSettings.HiddenRewardIds),
        nameof(AppSettings.SelectedTab),
        nameof(AppSettings.SettingsSection),
        nameof(AppSettings.WindowLeft),
        nameof(AppSettings.WindowTop),
        nameof(AppSettings.WindowWidth),
        nameof(AppSettings.WindowHeight),
    };

    public static string PendingPath(string dataDirectory) => Path.Combine(dataDirectory, PendingFileName);

    public static string BackupsFolder(string dataDirectory) => Path.Combine(dataDirectory, BackupsFolderName);

    public static IReadOnlyList<string> ConnectedServices(AppSettings settings)
    {
        var connected = new List<string>();
        if (settings.HasTwitchTokens) connected.Add(Twitch);
        if (settings.HasTokens) connected.Add(DonationAlerts);
        if (settings.HasDonatePayKey) connected.Add(DonatePay);
        if (settings.HasDonateXToken) connected.Add(DonateX);
        return connected;
    }

    public static ProfileResult Export(string path, string settingsJson, string dataDirectory, bool includeLists, string appVersion, DateTime nowUtc)
    {
        var tmp = path + ".tmp";
        try
        {
            var all = ParseObject(settingsJson);
            var shared = new JsonObject();
            foreach (var name in SharedSettings)
            {
                if (all.TryGetPropertyValue(name, out var value)) shared[name] = value?.DeepClone();
            }

            var info = new ProfileInfo
            {
                App = AppName,
                Format = Format,
                AppVersion = appVersion,
                CreatedUtc = nowUtc,
                TwitchUserId = Text(all, nameof(AppSettings.TwitchUserId)),
                TwitchLogin = Text(all, nameof(AppSettings.TwitchLogin)),
                Connected = ConnectedServices(all),
                HasLists = includeLists,
            };

            var missing = 0;
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var name in SoundFiles(shared))
                {
                    var source = Path.Combine(dataDirectory, SoundsFolder, name);
                    if (!File.Exists(source))
                    {
                        missing++;
                        continue;
                    }
                    zip.CreateEntryFromFile(source, SoundsPrefix + name, CompressionLevel.Optimal);
                    info.Sounds++;
                }
                if (includeLists)
                {
                    foreach (var list in ListFiles)
                    {
                        var source = Path.Combine(dataDirectory, list);
                        if (File.Exists(source)) zip.CreateEntryFromFile(source, ListsPrefix + list, CompressionLevel.Optimal);
                    }
                }
                WriteEntry(zip, SettingsEntry, shared.ToJsonString(Indented));
                WriteEntry(zip, InfoEntry, JsonSerializer.Serialize(info, Indented));
            }
            File.Move(tmp, path, true);
            return new ProfileResult(true, "", info, MissingSounds: missing);
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            TryDelete(tmp);
            return new ProfileResult(false, ex.Message);
        }
    }

    public static ProfileResult Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return Validate(zip, out _);
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            return new ProfileResult(false, NotAProfile);
        }
    }

    public static void Stage(string profilePath, string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        File.Copy(profilePath, PendingPath(dataDirectory), true);
    }

    public static void Unstage(string dataDirectory) => TryDelete(PendingPath(dataDirectory));

    public static ProfileResult? ApplyPending(string dataDirectory, string appVersion, DateTime now)
    {
        var pending = PendingPath(dataDirectory);
        if (!File.Exists(pending)) return null;
        try
        {
            return Apply(pending, dataDirectory, appVersion, now);
        }
        finally
        {
            TryDelete(pending);
        }
    }

    public static ProfileResult Apply(string profilePath, string dataDirectory, string appVersion, DateTime now)
    {
        string? backup = null;
        try
        {
            using var zip = ZipFile.OpenRead(profilePath);
            var check = Validate(zip, out var incoming);
            if (!check.Success) return check;

            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            var current = ReadCurrent(settingsPath);

            Directory.CreateDirectory(BackupsFolder(dataDirectory));
            backup = Path.Combine(BackupsFolder(dataDirectory), $"До загрузки профиля {now:yyyy-MM-dd HH-mm-ss}{Extension}");
            var saved = Export(backup, current.ToJsonString(), dataDirectory, includeLists: true, appVersion, now.ToUniversalTime());
            if (!saved.Success)
            {
                return new ProfileResult(false, "Не удалось сделать резервную копию текущих настроек, профиль не загружен: " + saved.Error, check.Info);
            }

            foreach (var entry in zip.Entries)
            {
                if (SoundName(entry.FullName) is { } sound)
                {
                    Directory.CreateDirectory(Path.Combine(dataDirectory, SoundsFolder));
                    WriteAtomically(Path.Combine(dataDirectory, SoundsFolder, sound), ReadBytes(entry, Ui.RewardAlertService.MaxBytes));
                }
                else if (ListName(entry.FullName) is { } list)
                {
                    var bytes = ReadBytes(entry, MaxListEntry);
                    if (JsonNode.Parse(bytes) is JsonArray) WriteAtomically(Path.Combine(dataDirectory, list), bytes);
                }
            }

            WriteAtomically(settingsPath, Encoding.UTF8.GetBytes(Merge(current, incoming)));
            PruneBackups(dataDirectory);
            return new ProfileResult(true, "", check.Info, backup);
        }
        catch (Exception ex) when (IsFileProblem(ex))
        {
            return new ProfileResult(false, "Не удалось загрузить профиль: " + ex.Message, BackupPath: backup != null && File.Exists(backup) ? backup : null);
        }
    }

    internal static string Merge(JsonObject current, JsonObject incoming)
    {
        var defaults = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject();
        var merged = (JsonObject)current.DeepClone();
        foreach (var name in SharedSettings)
        {
            if (!incoming.TryGetPropertyValue(name, out var value)) continue;
            if (value == null && defaults[name] != null) continue;
            merged[name] = value?.DeepClone();
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(merged.ToJsonString()) ?? new AppSettings();
        return JsonSerializer.Serialize(settings, Indented);
    }

    private static ProfileResult Validate(ZipArchive zip, out JsonObject settings)
    {
        settings = new JsonObject();
        var infoEntry = zip.GetEntry(InfoEntry);
        var settingsEntry = zip.GetEntry(SettingsEntry);
        if (infoEntry == null || settingsEntry == null) return new ProfileResult(false, NotAProfile);

        var info = JsonSerializer.Deserialize<ProfileInfo>(ReadBytes(infoEntry, MaxSmallEntry));
        if (info == null || info.App != AppName || info.Format < 1) return new ProfileResult(false, NotAProfile);
        if (info.Format > Format)
        {
            return new ProfileResult(false, $"Профиль сохранён в более новой версии StreamHelper ({info.AppVersion}). Обнови программу и попробуй снова.", info);
        }

        settings = ParseObject(Encoding.UTF8.GetString(ReadBytes(settingsEntry, MaxSmallEntry)));
        Merge(new JsonObject(), settings);
        info.Connected ??= new List<string>();
        info.HasLists = zip.Entries.Any(e => ListName(e.FullName) != null);
        info.Sounds = zip.Entries.Count(e => SoundName(e.FullName) != null);
        return new ProfileResult(true, "", info);
    }

    private static JsonObject ReadCurrent(string settingsPath)
    {
        try
        {
            return File.Exists(settingsPath) ? ParseObject(File.ReadAllText(settingsPath)) : new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static List<string> ConnectedServices(JsonObject all)
    {
        var connected = new List<string>();
        if (Text(all, nameof(AppSettings.TwitchRefreshTokenProtected)).Length > 0 && Text(all, nameof(AppSettings.TwitchUserId)).Length > 0) connected.Add(Twitch);
        if (Text(all, nameof(AppSettings.AccessTokenProtected)).Length > 0 || Text(all, nameof(AppSettings.RefreshTokenProtected)).Length > 0) connected.Add(DonationAlerts);
        if (Text(all, nameof(AppSettings.DonatePayKeyProtected)).Length > 0) connected.Add(DonatePay);
        if (Text(all, nameof(AppSettings.DonateXTokenProtected)).Length > 0) connected.Add(DonateX);
        return connected;
    }

    private static IEnumerable<string> SoundFiles(JsonObject shared)
    {
        if (shared[nameof(AppSettings.RewardSounds)] is not JsonObject sounds) return Array.Empty<string>();
        return sounds
            .Select(pair => pair.Value is JsonObject sound ? Text(sound, nameof(RewardSound.File)) : "")
            .Select(file => Path.GetFileName(file))
            .Where(IsSoundFileName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? SoundName(string entryName)
    {
        if (!entryName.StartsWith(SoundsPrefix, StringComparison.Ordinal)) return null;
        var name = entryName[SoundsPrefix.Length..];
        return IsSoundFileName(name) ? name : null;
    }

    private static string? ListName(string entryName)
    {
        if (!entryName.StartsWith(ListsPrefix, StringComparison.Ordinal)) return null;
        var name = entryName[ListsPrefix.Length..];
        return ListFiles.Contains(name, StringComparer.Ordinal) ? name : null;
    }

    private static bool IsSoundFileName(string name) =>
        name.Length > 0 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !name.Contains("..", StringComparison.Ordinal) &&
        Ui.RewardAlertService.Extensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    private static void PruneBackups(string dataDirectory)
    {
        try
        {
            var old = new DirectoryInfo(BackupsFolder(dataDirectory)).GetFiles("*" + Extension)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeptBackups);
            foreach (var file in old) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Removing old profile backups failed: " + ex.Message);
        }
    }

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Ожидался объект настроек.");

    private static string Text(JsonObject source, string name) =>
        source[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry, long max)
    {
        var tooBig = $"Файл «{entry.Name}» в профиле слишком большой.";
        if (entry.Length > max) throw new InvalidDataException(tooBig);
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > max) throw new InvalidDataException(tooBig);
        }
        return buffer.ToArray();
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, true);
    }

    private static bool IsFileProblem(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException or ArgumentException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Removing " + Path.GetFileName(path) + " failed: " + ex.Message);
        }
    }
}
