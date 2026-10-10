using System.Text.Json;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class VersionChoiceTests
{
    private static string Release(string tag, bool withExe = true, bool digest = true, bool prerelease = false, bool draft = false, string published = "2026-10-01T12:00:00Z") =>
        "{\"tag_name\":\"" + tag + "\",\"name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (prerelease ? "true" : "false") +
        ",\"published_at\":\"" + published + "\",\"body\":\"notes " + tag + "\",\"html_url\":\"https://x/" + tag + "\",\"assets\":[" +
        (withExe ? "{\"name\":\"StreamHelper.exe\",\"size\":10,\"browser_download_url\":\"https://x/" + tag + "/StreamHelper.exe\"" +
                   (digest ? ",\"digest\":\"sha256:" + new string('a', 64) + "\"" : "") + "}" : "") + "]}";

    private static UpdateRelease R(string version) => new(Version.Parse(version), "v" + version, "", "", "", null, null);

    [Fact]
    public void The_list_keeps_installable_releases_from_the_oldest_allowed_one_newest_first()
    {
        var json = "[" + string.Join(",",
            Release("v1.0.0.3", published: "2026-10-20T08:00:00Z"),
            Release("v1.0.0.4", prerelease: true),
            Release("v1.0.0.5", draft: true),
            Release("v1.0.0.2", withExe: false),
            Release("v.1.0.0.1"),
            Release("v.1.0.0.0"),
            Release("nightly"),
            Release("v1.0.0.6", digest: false)) + "]";

        var choosable = AppUpdate.Choosable(AppUpdate.ParseReleases(json));

        Assert.Equal(new[] { "1.0.0.3", "1.0.0.1" }, choosable.Select(r => r.Version.ToString(4)).ToArray());
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc), choosable[0].PublishedUtc);
        Assert.Equal("notes v1.0.0.3", choosable[0].Notes);
        Assert.Equal(new Version(1, 0, 0, 1), AppUpdate.OldestChoosable);
        Assert.EndsWith("/repos/NightFuryoOo/StreamHelper/releases?per_page=100", AppUpdate.ReleasesUrl(null));
    }

    [Fact]
    public void A_release_with_only_a_checksum_file_is_still_installable()
    {
        var release = new UpdateRelease(new Version(1, 0, 0, 2), "v1.0.0.2", "", "", "",
            new UpdateAsset("StreamHelper.exe", "https://x/e", 1), new UpdateAsset("StreamHelper.exe.sha256", "https://x/s", 1));

        Assert.True(release.CanInstall);
        Assert.False((release with { Checksum = null }).CanInstall);
    }

    [Theory]
    [InlineData("1.0.0.5", "1.0.0.3", "", true)]
    [InlineData("1.0.0.5", "1.0.0.3", "1.0.0.5", false)]
    [InlineData("1.0.0.6", "1.0.0.3", "1.0.0.5", true)]
    [InlineData("1.0.0.5", "1.0.0.5", "", false)]
    [InlineData("1.0.0.5", "1.0.0.3", "garbage", true)]
    public void The_banner_skips_the_version_it_was_told_to_forget_until_a_newer_one(string latest, string current, string skipped, bool offered)
    {
        Assert.Equal(offered, AppUpdate.ShouldOffer(R(latest), Version.Parse(current), skipped));
    }

    [Fact]
    public void Going_back_by_hand_forgets_the_newest_version_and_going_to_the_newest_forgets_nothing()
    {
        var known = new[] { R("1.0.0.5"), R("1.0.0.3"), R("1.0.0.1") };

        Assert.Equal("1.0.0.5", AppUpdate.SkipAfterChoosing(new Version(1, 0, 0, 3), known));
        Assert.Null(AppUpdate.SkipAfterChoosing(new Version(1, 0, 0, 5), known));
        Assert.Null(AppUpdate.SkipAfterChoosing(new Version(1, 0, 0, 2), Array.Empty<UpdateRelease>()));
    }

    [Fact]
    public void Rows_say_which_one_is_installed_and_what_the_button_does()
    {
        var current = new Version(1, 0, 0, 3);
        var older = new VersionRow(R("1.0.0.1"), current);
        var same = new VersionRow(R("1.0.0.3"), current);
        var newer = new VersionRow(R("1.0.0.5"), current);

        Assert.Equal(("старее", "установлена", "новее"), (older.Mark, same.Mark, newer.Mark));
        Assert.True(older.IsOlder);
        Assert.Equal("Установить 1.0.0.1", older.InstallText(false));
        Assert.Equal("Да, установить 1.0.0.1", older.InstallText(true));
        Assert.Equal("Уже установлена", same.InstallText(false));
        Assert.Contains("1.0.0.1", VersionRow.Hint);
    }

    [Fact]
    public void The_skipped_version_stays_on_this_pc()
    {
        Assert.Contains(nameof(AppSettings.UpdateSkipVersion), Profile.LocalSettings);
        Assert.Equal("", new AppSettings().UpdateSkipVersion);
    }
}

public class ForwardCompatibleDataTests
{
    [Fact]
    public void Settings_fields_from_a_newer_version_survive_a_save_by_this_one()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{\"ToastSeconds\":9,\"FutureOption\":true,\"FutureList\":[1,2]}");

        var store = new SettingsStore(path);
        store.Current.ToastSeconds = 11;
        store.Save();

        var saved = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.Equal(11, saved.GetProperty("ToastSeconds").GetInt32());
        Assert.True(saved.GetProperty("FutureOption").GetBoolean());
        Assert.Equal(2, saved.GetProperty("FutureList").GetArrayLength());
    }

    [Fact]
    public void List_items_keep_fields_a_newer_version_added()
    {
        using var dir = new TempDir();
        var path = dir.File("pings.json");
        File.WriteAllText(path, "[{\"Key\":\"a\",\"Login\":\"x\",\"Message\":\"m\",\"AtUtc\":\"2026-10-10T00:00:00Z\",\"Seen\":false,\"FutureTag\":\"vip\"}]");

        var store = new PingStore(path);
        store.Items[0].Seen = true;

        var saved = File.ReadAllText(path);
        Assert.Contains("\"FutureTag\": \"vip\"", saved);
        Assert.Contains("\"Seen\": true", saved);
    }

    [Fact]
    public void A_backup_before_switching_versions_keeps_settings_and_lists()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("settings.json"), "{\"ToastSeconds\":13}");
        File.WriteAllText(dir.File("pings.json"), "[]");

        var result = Profile.Backup(dir.Path, "До смены версии", "1.0.0.3", new DateTime(2026, 10, 10, 2, 0, 0));

        Assert.True(result.Success, result.Error);
        Assert.Equal("До смены версии 2026-10-10 02-00-00.shprofile", Path.GetFileName(result.BackupPath));
        Assert.True(Profile.Read(result.BackupPath!).Success);
    }
}
