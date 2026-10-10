using System.Security.Cryptography;
using System.Text;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class AppUpdateTests
{
    private const string Release = """
        {
          "tag_name": "v1.0.0.1",
          "name": "StreamHelper 1.0.0.1",
          "html_url": "https://github.com/NightFuryoOo/StreamHelper/releases/tag/v1.0.0.1",
          "draft": false,
          "prerelease": false,
          "body": "## Что нового\r\n\r\n\r\n- **Переименование** наград\r\n- Исправления\r\n",
          "assets": [
            { "name": "streamhelper.exe", "size": 68826175, "digest": "sha256:ec3bf0871540301914310bdb4dd204ca05b44e32bd12b8176a8d3ae9eed9145e", "browser_download_url": "https://github.com/x/StreamHelper.exe" },
            { "name": "StreamHelper.exe.sha256", "size": 84, "browser_download_url": "https://github.com/x/StreamHelper.exe.sha256" },
            { "name": "notes.txt", "size": 1, "browser_download_url": "https://github.com/x/notes.txt" }
          ]
        }
        """;

    [Theory]
    [InlineData("v1.0.0.1", "1.0.0.1")]
    [InlineData("V2.0.0", "2.0.0.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData(" v1.0.0.0 ", "1.0.0.0")]
    [InlineData("v.1.0.0.1", "1.0.0.1")]
    [InlineData("v-1.0.0.2", "1.0.0.2")]
    [InlineData("V 1.0.0.3", "1.0.0.3")]
    public void Tags_become_four_part_versions(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), AppUpdate.ParseVersion(tag));
    }

    [Theory]
    [InlineData("beta")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("v1")]
    public void Tags_that_are_not_versions_are_ignored(string? tag)
    {
        Assert.Null(AppUpdate.ParseVersion(tag));
    }

    [Theory]
    [InlineData("1.0.0.1", "1.0.0.0", true)]
    [InlineData("1.0.1", "1.0.0.9", true)]
    [InlineData("1.0.0.0", "1.0.0.0", false)]
    [InlineData("1.0", "1.0.0.0", false)]
    [InlineData("0.9.9.9", "1.0.0.0", false)]
    public void Only_a_higher_version_counts_as_new(string remote, string current, bool newer)
    {
        var release = new UpdateRelease(AppUpdate.ParseVersion(remote)!, remote, "", "", "", null, null);

        Assert.Equal(newer, AppUpdate.IsNewer(release, Version.Parse(current)));
    }

    [Fact]
    public void A_release_gives_its_version_page_notes_and_both_files()
    {
        var release = AppUpdate.ParseRelease(Release)!;

        Assert.Equal(new Version(1, 0, 0, 1), release.Version);
        Assert.Equal("StreamHelper 1.0.0.1", release.Name);
        Assert.EndsWith("/releases/tag/v1.0.0.1", release.PageUrl);
        Assert.Equal("Что нового\n\n- Переименование наград\n- Исправления", release.Notes);
        Assert.Equal("https://github.com/x/StreamHelper.exe", release.Exe!.Url);
        Assert.Equal(68826175, release.Exe.Size);
        Assert.Equal("https://github.com/x/StreamHelper.exe.sha256", release.Checksum!.Url);
        Assert.Equal("EC3BF0871540301914310BDB4DD204CA05B44E32BD12B8176A8D3AE9EED9145E", release.Exe.Digest);
        Assert.Null(release.Checksum.Digest);
    }

    [Theory]
    [InlineData("sha256:ec3bf0871540301914310bdb4dd204ca05b44e32bd12b8176a8d3ae9eed9145e", "EC3BF0871540301914310BDB4DD204CA05B44E32BD12B8176A8D3AE9EED9145E")]
    [InlineData("SHA256:EC3BF0871540301914310BDB4DD204CA05B44E32BD12B8176A8D3AE9EED9145E", "EC3BF0871540301914310BDB4DD204CA05B44E32BD12B8176A8D3AE9EED9145E")]
    [InlineData("sha512:ec3bf0871540301914310bdb4dd204ca05b44e32bd12b8176a8d3ae9eed9145e", null)]
    [InlineData("sha256:1234", null)]
    [InlineData("", null)]
    public void GitHub_digests_give_the_sha256_of_the_file(string digest, string? expected)
    {
        Assert.Equal(expected, AppUpdate.ParseDigest(digest));
    }

    [Theory]
    [InlineData("\"draft\": false", "\"draft\": true")]
    [InlineData("\"prerelease\": false", "\"prerelease\": true")]
    [InlineData("\"tag_name\": \"v1.0.0.1\"", "\"tag_name\": \"nightly\"")]
    public void Drafts_prereleases_and_odd_tags_are_not_offered(string from, string to)
    {
        Assert.Null(AppUpdate.ParseRelease(Release.Replace(from, to)));
    }

    [Fact]
    public void A_release_without_files_still_parses_but_has_nothing_to_install()
    {
        var release = AppUpdate.ParseRelease("{\"tag_name\":\"1.0.0.5\",\"assets\":[]}")!;

        Assert.Null(release.Exe);
        Assert.Null(release.Checksum);
        Assert.Equal("", release.Notes);
    }

    [Fact]
    public void Long_notes_are_cut()
    {
        var notes = AppUpdate.CleanNotes(new string('а', 5000));

        Assert.Equal(AppUpdate.MaxNotesLength + 1, notes.Length);
        Assert.EndsWith("…", notes);
    }

    [Fact]
    public void The_checksum_is_read_from_the_usual_sha256_file_formats()
    {
        var hash = new string('a', 32) + new string('F', 32);

        Assert.Equal(hash.ToUpperInvariant(), AppUpdate.ParseChecksum(hash + "  StreamHelper.exe\n"));
        Assert.Equal(hash.ToUpperInvariant(), AppUpdate.ParseChecksum("SHA256 (StreamHelper.exe) = " + hash));
        Assert.Null(AppUpdate.ParseChecksum("no hash here"));
        Assert.Null(AppUpdate.ParseChecksum(hash + "0"));
    }

    [Fact]
    public void The_latest_release_address_points_at_the_public_repository()
    {
        Assert.Equal("https://api.github.com/repos/NightFuryoOo/StreamHelper/releases/latest", AppUpdate.LatestUrl(null));
        Assert.Equal("http://127.0.0.1:5/repos/NightFuryoOo/StreamHelper/releases/latest", AppUpdate.LatestUrl("http://127.0.0.1:5/"));
    }

    [Fact]
    public void The_app_version_has_four_parts()
    {
        Assert.Equal(4, AppVersion.Current.Split('.').Length);
        Assert.Equal(new Version(1, 2, 0, 0), AppVersion.Normalize(new Version(1, 2)));
    }
}

public class UpdateInstallerTests
{
    [Fact]
    public void The_new_file_takes_the_exe_name_and_the_old_one_is_kept_aside()
    {
        using var dir = new TempDir();
        var exe = dir.File("StreamHelper.exe");
        File.WriteAllText(exe, "old");
        File.WriteAllText(UpdateInstaller.NewPath(exe), "new");

        UpdateInstaller.Swap(exe, UpdateInstaller.NewPath(exe));

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(UpdateInstaller.OldPath(exe)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(exe)));
    }

    [Fact]
    public void When_the_new_file_cannot_be_moved_in_the_old_exe_is_put_back()
    {
        using var dir = new TempDir();
        var exe = dir.File("StreamHelper.exe");
        File.WriteAllText(exe, "old");

        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Swap(exe, dir.File("missing.new")));

        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateInstaller.OldPath(exe)));
    }

    [Fact]
    public void A_leftover_from_an_earlier_update_does_not_block_the_next_one()
    {
        using var dir = new TempDir();
        var exe = dir.File("StreamHelper.exe");
        File.WriteAllText(exe, "v2");
        File.WriteAllText(UpdateInstaller.OldPath(exe), "v1");
        File.WriteAllText(UpdateInstaller.NewPath(exe), "v3");

        UpdateInstaller.Swap(exe, UpdateInstaller.NewPath(exe));

        Assert.Equal("v3", File.ReadAllText(exe));
        Assert.Equal("v2", File.ReadAllText(UpdateInstaller.OldPath(exe)));
    }

    [Fact]
    public void Clean_up_removes_the_old_and_half_downloaded_files_and_reports_a_locked_one()
    {
        using var dir = new TempDir();
        var exe = dir.File("StreamHelper.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(UpdateInstaller.OldPath(exe), "old");
        File.WriteAllText(UpdateInstaller.NewPath(exe), "partial");

        Assert.True(UpdateInstaller.CleanUp(exe));
        Assert.True(File.Exists(exe));
        Assert.False(File.Exists(UpdateInstaller.OldPath(exe)));
        Assert.False(File.Exists(UpdateInstaller.NewPath(exe)));

        File.WriteAllText(UpdateInstaller.OldPath(exe), "old");
        using (new FileStream(UpdateInstaller.OldPath(exe), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(UpdateInstaller.CleanUp(exe));
        }
        Assert.True(UpdateInstaller.CleanUp(exe));
    }

    [Fact]
    public void Only_a_single_exe_without_a_dll_beside_it_updates_itself()
    {
        using var dir = new TempDir();
        var exe = dir.File("StreamHelper.exe");

        Assert.True(UpdateInstaller.IsInstallable(exe, dir.Path, "StreamHelper"));
        Assert.False(UpdateInstaller.IsInstallable(null, dir.Path, "StreamHelper"));
        Assert.False(UpdateInstaller.IsInstallable(dir.File("dotnet.dll"), dir.Path, "StreamHelper"));
        File.WriteAllText(dir.File("StreamHelper.dll"), "dev build");
        Assert.False(UpdateInstaller.IsInstallable(exe, dir.Path, "StreamHelper"));
    }

    [Fact]
    public async Task The_download_is_hashed_while_it_is_written_and_reports_progress()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var body = string.Concat(Enumerable.Repeat("StreamHelper update payload ", 20000));
        server.Handler = (_, _) => (200, body);
        using var http = new HttpClient();
        var seen = new List<int>();
        var target = dir.File("StreamHelper.exe.new");

        var hash = await UpdateInstaller.DownloadAsync(http, server.BaseUrl + "/StreamHelper.exe", target, new SyncProgress(seen), CancellationToken.None);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))), hash);
        Assert.Equal(Encoding.UTF8.GetBytes(body), File.ReadAllBytes(target));
        Assert.Equal(100, seen[^1]);
        Assert.Equal(seen.OrderBy(x => x), seen);
    }

    [Fact]
    public async Task A_failed_download_throws()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (_, _) => (404, "");
        using var http = new HttpClient();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UpdateInstaller.DownloadAsync(http, server.BaseUrl + "/x", dir.File("x.new"), null, CancellationToken.None));
    }

    [Fact]
    public void A_wrong_checksum_is_refused_and_the_case_does_not_matter()
    {
        UpdateInstaller.Verify("ABCDEF", "abcdef");

        Assert.Throws<ChecksumMismatchException>(() => UpdateInstaller.Verify("ABCDEF", "ABCDEE"));
    }

    [Fact]
    public void The_marker_says_once_which_version_was_installed()
    {
        using var dir = new TempDir();

        Assert.Null(UpdateInstaller.TakeMarker(dir.Path));
        UpdateInstaller.WriteMarker(dir.Path, "1.0.0.1");

        Assert.Equal(new UpdateMarker("1.0.0.1", false), UpdateInstaller.TakeMarker(dir.Path));
        Assert.Null(UpdateInstaller.TakeMarker(dir.Path));
    }

    [Fact]
    public void A_version_chosen_by_hand_is_marked_and_an_old_plain_marker_still_reads()
    {
        using var dir = new TempDir();

        UpdateInstaller.WriteMarker(dir.Path, "1.0.0.2", chosen: true);
        Assert.Equal(new UpdateMarker("1.0.0.2", true), UpdateInstaller.TakeMarker(dir.Path));

        File.WriteAllText(dir.File("update-done.txt"), "1.0.0.3\r\n");
        Assert.Equal(new UpdateMarker("1.0.0.3", false), UpdateInstaller.TakeMarker(dir.Path));
    }

    private sealed class SyncProgress : IProgress<int>
    {
        private readonly List<int> _seen;

        public SyncProgress(List<int> seen) => _seen = seen;

        public void Report(int value) => _seen.Add(value);
    }
}
