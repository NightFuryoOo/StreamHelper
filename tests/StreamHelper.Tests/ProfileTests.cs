using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ProfileTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 30, 0, DateTimeKind.Local);

    private static SettingsStore Source(TempDir dir)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Current;
        s.ToastSeconds = 17;
        s.ChatFontSize = 21;
        s.ChatLeft = 100;
        s.ChatTop = 200;
        s.WindowWidth = 480;
        s.PingWords = new List<string> { "босс", "hk*" };
        s.ChatHighlights["anna_k"] = "#FF8800";
        s.SetHotkey(HotkeyAction.Donations, AppSettings.ModControl, 0x70);
        s.ManagedRewardIds = new List<string> { "copy-1" };
        s.RewardCopies["orig-1"] = "copy-1";
        s.HiddenRewardIds = new List<string> { "orig-2" };
        s.RewardSounds["orig-1"] = new RewardSound { File = "r-orig-1.mp3", Name = "трек.mp3", Volume = 0.4 };
        s.SoundRewardIds = new List<string> { "orig-1" };
        s.TwitchUserId = "111";
        s.TwitchLogin = "source_channel";
        s.TwitchRefreshToken = "source-refresh";
        s.TwitchAccessToken = "source-access";
        s.AccessToken = "source-da";
        s.DonatePayKey = "source-dp";
        s.LastDonationId = 999;
        s.RewardIconsFolder = @"C:\source\icons";
        store.Save();
        Directory.CreateDirectory(Path.Combine(dir.Path, "alerts"));
        File.WriteAllBytes(Path.Combine(dir.Path, "alerts", "r-orig-1.mp3"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(dir.File("donations.json"), "[{\"Id\":5,\"Username\":\"src\"}]");
        File.WriteAllText(dir.File("redemptions.json"), "[]");
        return store;
    }

    private static SettingsStore Target(TempDir dir)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Current;
        s.ToastSeconds = 6;
        s.TwitchUserId = "222";
        s.TwitchLogin = "target_channel";
        s.TwitchRefreshToken = "target-refresh";
        s.AccessToken = "target-da";
        s.LastDonationId = 42;
        s.RewardIconsFolder = @"D:\target\icons";
        store.Save();
        File.WriteAllText(dir.File("donations.json"), "[{\"Id\":7,\"Username\":\"mine\"}]");
        return store;
    }

    private static string Export(TempDir dir, SettingsStore store, bool lists, string name = "p.shprofile")
    {
        var path = dir.File(name);
        var result = Profile.Export(path, store.ToJson(), dir.Path, lists, "1.2.3", Now.ToUniversalTime());
        Assert.True(result.Success, result.Error);
        return path;
    }

    private static string EntryText(string zipPath, string entry)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        using var reader = new StreamReader(zip.GetEntry(entry)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ProfileResult LoadInto(TempDir target, string profilePath)
    {
        Profile.Stage(profilePath, target.Path);
        return Profile.ApplyPending(target.Path, "1.2.3", Now)!;
    }

    [Fact]
    public void Every_setting_is_either_carried_over_or_kept_on_the_computer_and_never_both()
    {
        var names = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject().Select(p => p.Key).ToHashSet();

        Assert.Empty(Profile.LocalSettings.Intersect(Profile.SharedSettings));
        Assert.Equal(names.OrderBy(n => n), Profile.LocalSettings.Concat(Profile.SharedSettings).OrderBy(n => n));
    }

    [Fact]
    public void Keys_tokens_and_secrets_always_stay_on_the_computer()
    {
        var names = JsonSerializer.SerializeToNode(new AppSettings())!.AsObject().Select(p => p.Key);
        var secretLike = names.Where(n =>
            n.Contains("Protected") || n.Contains("Token") || n.Contains("Secret") || n.Contains("Key") || n.StartsWith("Twitch"));

        Assert.All(secretLike, n => Assert.Contains(n, Profile.LocalSettings));
    }

    [Fact]
    public void The_saved_file_has_no_keys_tokens_or_sync_marks_but_remembers_the_channel_and_services()
    {
        using var dir = new TempDir();
        var path = Export(dir, Source(dir), lists: false);

        var settings = EntryText(path, "settings.json");
        foreach (var name in Profile.LocalSettings) Assert.DoesNotContain("\"" + name + "\"", settings);
        Assert.DoesNotContain("source-", settings);
        Assert.Contains("\"ToastSeconds\": 17", settings);

        var info = JsonSerializer.Deserialize<ProfileInfo>(EntryText(path, "profile.json"))!;
        Assert.Equal("111", info.TwitchUserId);
        Assert.Equal("source_channel", info.TwitchLogin);
        Assert.Equal(new[] { Profile.Twitch, Profile.DonationAlerts, Profile.DonatePay }, info.Connected);
        Assert.Equal("1.2.3", info.AppVersion);
        Assert.Equal(1, info.Sounds);
        Assert.False(info.HasLists);
    }

    [Fact]
    public void Loading_brings_over_settings_rewards_and_sounds_and_keeps_this_computers_accounts()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        Target(target);

        var result = LoadInto(target, path);

        Assert.True(result.Success, result.Error);
        var s = new SettingsStore(target.File("settings.json")).Current;
        Assert.Equal(17, s.ToastSeconds);
        Assert.Equal(21, s.ChatFontSize);
        Assert.Equal(100, s.ChatLeft);
        Assert.Equal(480, s.WindowWidth);
        Assert.Equal(new[] { "босс", "hk*" }, s.PingWords);
        Assert.Equal("#FF8800", s.ChatHighlights["anna_k"]);
        Assert.Equal(0x70u, s.GetHotkey(HotkeyAction.Donations).VirtualKey);
        Assert.Equal("copy-1", s.RewardCopies["orig-1"]);
        Assert.Equal(new[] { "orig-2" }, s.HiddenRewardIds);
        Assert.Equal(0.4, s.RewardSounds["orig-1"].Volume);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(target.Path, "alerts", "r-orig-1.mp3")));

        Assert.Equal("222", s.TwitchUserId);
        Assert.Equal("target_channel", s.TwitchLogin);
        Assert.Equal("target-refresh", s.TwitchRefreshToken);
        Assert.Equal("target-da", s.AccessToken);
        Assert.Equal(42, s.LastDonationId);
        Assert.Equal(@"D:\target\icons", s.RewardIconsFolder);
        Assert.False(s.HasDonatePayKey);
    }

    [Fact]
    public void Without_lists_this_computers_lists_stay_as_they_were()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        Target(target);

        LoadInto(target, path);

        Assert.Contains("mine", File.ReadAllText(target.File("donations.json")));
        Assert.False(File.Exists(target.File("redemptions.json")));
    }

    [Fact]
    public void With_lists_the_lists_are_replaced_by_the_saved_ones()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: true);
        Target(target);

        var read = Profile.Read(path);
        LoadInto(target, path);

        Assert.True(read.Info!.HasLists);
        Assert.Contains("src", File.ReadAllText(target.File("donations.json")));
        Assert.Equal("[]", File.ReadAllText(target.File("redemptions.json")));
    }

    [Fact]
    public void Loading_first_saves_the_old_state_and_that_backup_brings_it_back()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        Target(target);

        var result = LoadInto(target, path);

        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.StartsWith(Profile.BackupsFolder(target.Path), result.BackupPath);
        var backup = Profile.Read(result.BackupPath!);
        Assert.True(backup.Success);
        Assert.True(backup.Info!.HasLists);

        var undo = LoadInto(target, result.BackupPath!);

        Assert.True(undo.Success, undo.Error);
        var s = new SettingsStore(target.File("settings.json")).Current;
        Assert.Equal(6, s.ToastSeconds);
        Assert.Contains("mine", File.ReadAllText(target.File("donations.json")));
        Assert.Equal("target-refresh", s.TwitchRefreshToken);
    }

    [Fact]
    public void Only_the_newest_backups_are_kept()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        Target(target);

        for (var i = 0; i < Profile.KeptBackups + 3; i++)
        {
            Profile.Stage(path, target.Path);
            Assert.True(Profile.ApplyPending(target.Path, "1.2.3", Now.AddSeconds(i))!.Success);
        }

        Assert.Equal(Profile.KeptBackups, Directory.GetFiles(Profile.BackupsFolder(target.Path), "*" + Profile.Extension).Length);
    }

    [Fact]
    public void The_staged_file_is_removed_after_loading_and_nothing_happens_without_one()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);

        Assert.Null(Profile.ApplyPending(target.Path, "1.2.3", Now));
        LoadInto(target, path);

        Assert.False(File.Exists(Profile.PendingPath(target.Path)));
        Assert.Null(Profile.ApplyPending(target.Path, "1.2.3", Now));
    }

    [Fact]
    public void A_profile_from_a_newer_version_is_refused_and_nothing_changes()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        RewriteEntry(path, "profile.json", text => text.Replace("\"Format\": 1", "\"Format\": 99"));
        Target(target);

        var read = Profile.Read(path);
        var result = LoadInto(target, path);

        Assert.False(read.Success);
        Assert.Contains("Обнови программу", read.Error);
        Assert.False(result.Success);
        Assert.Equal(6, new SettingsStore(target.File("settings.json")).Current.ToastSeconds);
        Assert.False(Directory.Exists(Profile.BackupsFolder(target.Path)));
        Assert.False(File.Exists(Profile.PendingPath(target.Path)));
    }

    [Fact]
    public void Something_that_is_not_a_profile_is_refused()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("junk.shprofile"), "not a zip");
        var zipped = dir.File("other.shprofile");
        using (var zip = ZipFile.Open(zipped, ZipArchiveMode.Create)) zip.CreateEntry("hello.txt");

        Assert.Contains("не файл профиля", Profile.Read(dir.File("junk.shprofile")).Error);
        Assert.Contains("не файл профиля", Profile.Read(zipped).Error);
        Assert.False(Profile.Read(dir.File("missing.shprofile")).Success);
    }

    [Fact]
    public void Broken_setting_values_are_refused_before_anything_is_touched()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        RewriteEntry(path, "settings.json", text => text.Replace("\"ToastSeconds\": 17", "\"ToastSeconds\": \"много\""));
        Target(target);

        Assert.False(Profile.Read(path).Success);
        Assert.False(LoadInto(target, path).Success);
        Assert.Equal(6, new SettingsStore(target.File("settings.json")).Current.ToastSeconds);
    }

    [Fact]
    public void Files_outside_the_expected_places_are_never_written()
    {
        using var source = new TempDir();
        using var target = new TempDir();
        var path = Export(source, Source(source), lists: false);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            Write(zip, "alerts/../../evil.mp3", "x");
            Write(zip, "alerts/run.exe", "x");
            Write(zip, "lists/../settings.json", "[]");
            Write(zip, "lists/notes.json", "[]");
            Write(zip, "../outside.json", "x");
        }
        Directory.CreateDirectory(Path.Combine(target.Path, "inner"));
        var data = Path.Combine(target.Path, "inner");

        Profile.Stage(path, data);
        var result = Profile.ApplyPending(data, "1.2.3", Now)!;

        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(Path.Combine(target.Path, "evil.mp3")));
        Assert.False(File.Exists(Path.Combine(data, "evil.mp3")));
        Assert.False(File.Exists(Path.Combine(data, "alerts", "run.exe")));
        Assert.False(File.Exists(Path.Combine(data, "lists", "notes.json")));
        Assert.False(File.Exists(Path.Combine(data, "notes.json")));
        Assert.False(File.Exists(Path.Combine(target.Path, "outside.json")));
        Assert.True(File.Exists(Path.Combine(data, "alerts", "r-orig-1.mp3")));
    }

    [Fact]
    public void Empty_values_for_required_settings_do_not_wipe_them()
    {
        var current = JsonSerializer.SerializeToNode(new AppSettings { ToastSeconds = 9 })!.AsObject();
        var incoming = new JsonObject { ["RewardSounds"] = null, ["PingWords"] = null, ["ChatLeft"] = null, ["ToastSeconds"] = 12 };
        current["ChatLeft"] = 50;

        var merged = JsonSerializer.Deserialize<AppSettings>(Profile.Merge(current, incoming))!;

        Assert.NotNull(merged.RewardSounds);
        Assert.NotNull(merged.PingWords);
        Assert.Null(merged.ChatLeft);
        Assert.Equal(12, merged.ToastSeconds);
    }

    [Fact]
    public void Sounds_whose_files_are_gone_are_counted_and_the_rest_is_saved()
    {
        using var dir = new TempDir();
        var store = Source(dir);
        store.Current.RewardSounds["orig-3"] = new RewardSound { File = "r-orig-3.wav", Name = "нет.wav" };
        store.Save();

        var result = Profile.Export(dir.File("p.shprofile"), store.ToJson(), dir.Path, false, "1.2.3", Now.ToUniversalTime());

        Assert.True(result.Success);
        Assert.Equal(1, result.MissingSounds);
        Assert.Equal(1, result.Info!.Sounds);
        Assert.Contains("Не найдено файлов звуков: 1", ProfileTexts.Saved("p.shprofile", result));
    }

    [Fact]
    public void The_confirmation_names_the_date_channel_and_lists_and_warns_about_another_channel()
    {
        var info = new ProfileInfo { CreatedUtc = Now.ToUniversalTime(), TwitchUserId = "111", TwitchLogin = "source_channel", HasLists = true };

        var same = ProfileTexts.Confirm(info, "111");
        var other = ProfileTexts.Confirm(info, "222");
        var none = ProfileTexts.Confirm(info, "");

        Assert.StartsWith("Загрузить профиль от 09.10.2026 12:30, канал source_channel, со списками?", same);
        Assert.Contains("программа перезапустится", same);
        Assert.DoesNotContain("другого канала", same);
        Assert.Contains("другого канала", other);
        Assert.DoesNotContain("другого канала", none);
    }

    [Fact]
    public void After_loading_the_notice_lists_the_services_to_reconnect()
    {
        var info = new ProfileInfo
        {
            TwitchUserId = "111", TwitchLogin = "source_channel",
            Connected = new List<string> { Profile.Twitch, Profile.DonationAlerts, Profile.DonateX },
        };
        var result = new ProfileResult(true, "", info, @"C:\b.shprofile");
        var fresh = new AppSettings();

        var notice = ProfileTexts.Imported(result, fresh);

        Assert.Equal(
            "Профиль загружен. Прежние настройки сохранены в резервную копию.\nПодключи заново: Twitch (канал source_channel), DonationAlerts, DonateX.",
            notice.Text);
        Assert.Equal(@"C:\b.shprofile", notice.BackupPath);
    }

    [Fact]
    public void After_loading_on_another_channel_the_notice_warns_about_rewards()
    {
        var info = new ProfileInfo { TwitchUserId = "111", TwitchLogin = "source_channel", Connected = new List<string> { Profile.Twitch } };
        var current = new AppSettings { TwitchUserId = "222" };
        current.TwitchRefreshToken = "r";

        var notice = ProfileTexts.Imported(new ProfileResult(true, "", info, null), current);

        Assert.Contains("сохранён с канала source_channel, а подключён другой", notice.Text);
        Assert.DoesNotContain("Подключи заново", notice.Text);
    }

    [Fact]
    public void On_the_same_computer_the_notice_asks_for_nothing()
    {
        var info = new ProfileInfo { TwitchUserId = "111", TwitchLogin = "c", Connected = new List<string> { Profile.Twitch } };
        var current = new AppSettings { TwitchUserId = "111" };
        current.TwitchRefreshToken = "r";

        var notice = ProfileTexts.Imported(new ProfileResult(true, "", info, null), current);

        Assert.Equal("Профиль загружен. Прежние настройки сохранены в резервную копию.", notice.Text);
    }

    [Fact]
    public void A_failed_load_shows_the_reason()
    {
        var notice = ProfileTexts.Imported(new ProfileResult(false, "Не удалось загрузить профиль: диск полон"), new AppSettings());

        Assert.Equal("Не удалось загрузить профиль: диск полон", notice.Text);
        Assert.Null(notice.BackupPath);
    }

    [Fact]
    public void The_suggested_file_name_has_the_date()
    {
        Assert.Equal("StreamHelper профиль 2026-10-09.shprofile", ProfileTexts.DefaultFileName(Now));
    }

    private static void RewriteEntry(string zipPath, string entry, Func<string, string> change)
    {
        var text = change(EntryText(zipPath, entry));
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        zip.GetEntry(entry)!.Delete();
        Write(zip, entry, text);
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }
}
