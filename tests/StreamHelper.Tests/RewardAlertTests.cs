using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RewardAlertTests
{
    private const string RewardA = "11111111-aaaa-4bbb-8ccc-000000000001";
    private const string RewardB = "22222222-aaaa-4bbb-8ccc-000000000002";

    private static string Source(TempDir dir, string name, int bytes = 2048)
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, bytes).ToArray());
        return path;
    }

    private static (RewardAlertService Service, SettingsStore Settings, string Folder) Make(TempDir dir)
    {
        var settings = new SettingsStore(dir.File("settings.json"));
        var folder = dir.File("alerts");
        return (new RewardAlertService(settings, folder), settings, folder);
    }

    [Fact]
    public void A_chosen_file_is_copied_under_the_name_of_its_reward_and_the_extension_may_be_in_any_case()
    {
        using var dir = new TempDir();
        var folder = dir.File("alerts");

        var result = RewardAlertService.Import(Source(dir, "Hype Horn.MP3"), folder, RewardA);

        Assert.True(result.Success);
        Assert.Equal($"r-{RewardA}.mp3", result.StoredName);
        Assert.Equal(2048, new FileInfo(Path.Combine(folder, result.StoredName)).Length);
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void A_new_choice_replaces_the_old_one_of_that_reward_whatever_its_extension_and_leaves_other_rewards_alone()
    {
        using var dir = new TempDir();
        var folder = dir.File("alerts");
        Assert.True(RewardAlertService.Import(Source(dir, "a.wav"), folder, RewardA).Success);
        Assert.True(RewardAlertService.Import(Source(dir, "other.mp3", 300), folder, RewardB).Success);

        var second = RewardAlertService.Import(Source(dir, "b.m4a", 4096), folder, RewardA);

        Assert.Equal($"r-{RewardA}.m4a", second.StoredName);
        Assert.Equal(
            new[] { $"r-{RewardA}.m4a", $"r-{RewardB}.mp3" }.Order().ToArray(),
            Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray());
        Assert.True(RewardAlertService.Import(Source(dir, "c.m4a", 100), folder, RewardA).Success);
        Assert.Equal(100, new FileInfo(Path.Combine(folder, $"r-{RewardA}.m4a")).Length);
        Assert.Equal(300, new FileInfo(Path.Combine(folder, $"r-{RewardB}.mp3")).Length);
    }

    [Fact]
    public void Files_that_cannot_work_are_refused_with_a_reason_and_nothing_is_copied()
    {
        using var dir = new TempDir();
        var folder = dir.File("alerts");

        var missing = RewardAlertService.Import(dir.File("nope.mp3"), folder, RewardA);
        var wrong = RewardAlertService.Import(Source(dir, "picture.png"), folder, RewardA);
        var empty = RewardAlertService.Import(Source(dir, "empty.mp3", 0), folder, RewardA);
        var bigPath = dir.File("big.wav");
        using (var big = File.Create(bigPath)) big.SetLength(RewardAlertService.MaxBytes + 1);
        var tooBig = RewardAlertService.Import(bigPath, folder, RewardA);
        var noReward = RewardAlertService.Import(Source(dir, "fine.mp3"), folder, " ");

        Assert.False(missing.Success);
        Assert.Equal("Файл не найден.", missing.Error);
        Assert.False(wrong.Success);
        Assert.Contains("mp3", wrong.Error);
        Assert.False(empty.Success);
        Assert.False(tooBig.Success);
        Assert.Contains("15 МБ", tooBig.Error);
        Assert.False(noReward.Success);
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
    }

    [Fact]
    public void An_id_with_characters_a_file_name_cannot_hold_still_gives_a_safe_name()
    {
        Assert.Equal("r-abc_def__", RewardAlertService.StoredBaseName("abc/def.."));
        Assert.Equal("r-" + new string('x', 80), RewardAlertService.StoredBaseName(new string('x', 200)));
    }

    [Fact]
    public void The_volume_is_kept_between_zero_and_one_and_starts_at_seventy_percent()
    {
        Assert.Equal(0.7, new RewardSound().Volume);
        Assert.Equal(0, RewardAlertService.ClampVolume(-3));
        Assert.Equal(1, RewardAlertService.ClampVolume(7));
        Assert.Equal(0.25, RewardAlertService.ClampVolume(0.25));
        Assert.Equal(0.7, RewardAlertService.ClampVolume(double.NaN));
    }

    [Fact]
    public void Nothing_plays_for_a_reward_without_a_sound_or_whose_file_has_gone()
    {
        using var dir = new TempDir();
        var (service, settings, _) = Make(dir);

        Assert.Null(service.SoundOf(RewardA));
        Assert.Null(service.PathOf(RewardA));
        Assert.False(service.Play(RewardA));

        settings.Current.RewardSounds[RewardA] = new RewardSound { File = "r-not-there.mp3", Name = "x.mp3" };
        Assert.NotNull(service.SoundOf(RewardA));
        Assert.Null(service.PathOf(RewardA));
        Assert.False(service.Play(RewardA));
    }

    [Fact]
    public void Each_reward_keeps_its_own_file_name_and_volume()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);

        Assert.True(service.Choose(RewardA, Source(dir, "Horn.mp3")).Success);
        Assert.True(service.Choose(RewardB, Source(dir, "Bell.wav", 900)).Success);
        service.SetVolume(RewardA, 0.25);
        service.SetVolume(RewardB, 3);

        Assert.Equal(("Horn.mp3", 0.25), (service.SoundOf(RewardA)!.Name, service.SoundOf(RewardA)!.Volume));
        Assert.Equal(("Bell.wav", 1.0), (service.SoundOf(RewardB)!.Name, service.SoundOf(RewardB)!.Volume));
        Assert.Equal(Path.Combine(folder, $"r-{RewardA}.mp3"), service.PathOf(RewardA));
        Assert.Equal(Path.Combine(folder, $"r-{RewardB}.wav"), service.PathOf(RewardB));
        Assert.Equal(2, settings.Current.RewardSounds.Count);
    }

    [Fact]
    public void Choosing_another_file_for_a_reward_keeps_its_volume_and_a_refused_file_changes_nothing()
    {
        using var dir = new TempDir();
        var (service, _, folder) = Make(dir);
        Assert.True(service.Choose(RewardA, Source(dir, "first.mp3")).Success);
        service.SetVolume(RewardA, 0.4);

        Assert.True(service.Choose(RewardA, Source(dir, "second.wav", 512)).Success);
        var refused = service.Choose(RewardA, Source(dir, "notes.txt"));

        Assert.False(refused.Success);
        var sound = service.SoundOf(RewardA)!;
        Assert.Equal(("second.wav", 0.4), (sound.Name, sound.Volume));
        Assert.Equal(new[] { $"r-{RewardA}.wav" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void Taking_a_sound_off_removes_only_that_rewards_file_and_entry()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);
        service.Choose(RewardA, Source(dir, "a.mp3"));
        service.Choose(RewardB, Source(dir, "b.mp3"));

        service.Clear(RewardA);

        Assert.Null(service.SoundOf(RewardA));
        Assert.NotNull(service.SoundOf(RewardB));
        Assert.Equal(new[] { $"r-{RewardB}.mp3" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.Equal(new[] { RewardB }, settings.Current.RewardSounds.Keys.ToArray());
    }

    [Fact]
    public void A_reward_and_its_copy_share_one_sound_stored_under_the_original()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);
        settings.Current.RewardCopies[RewardA] = RewardB;

        Assert.Equal(RewardA, RewardAlertService.SoundKey(settings.Current.RewardCopies, RewardB));
        Assert.Equal(RewardA, RewardAlertService.SoundKey(settings.Current.RewardCopies, RewardA));
        Assert.Equal("other", RewardAlertService.SoundKey(settings.Current.RewardCopies, "other"));

        Assert.True(service.Choose(RewardB, Source(dir, "Horn.mp3")).Success);

        Assert.Equal(new[] { RewardA }, settings.Current.RewardSounds.Keys.ToArray());
        Assert.Equal(Path.Combine(folder, $"r-{RewardA}.mp3"), service.PathOf(RewardA));
        Assert.Equal(service.PathOf(RewardA), service.PathOf(RewardB));
        Assert.Same(service.SoundOf(RewardA), service.SoundOf(RewardB));
        service.SetVolume(RewardB, 0.3);
        Assert.Equal(0.3, service.SoundOf(RewardA)!.Volume);
    }

    [Fact]
    public void Deleting_the_copy_keeps_the_sound_of_the_original_but_taking_the_sound_off_clears_both()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);
        settings.Current.RewardCopies[RewardA] = RewardB;
        service.Choose(RewardA, Source(dir, "Horn.mp3"));

        service.Forget(new[] { RewardB });

        Assert.NotNull(service.SoundOf(RewardA));
        Assert.NotNull(service.SoundOf(RewardB));
        Assert.Single(Directory.GetFiles(folder));

        service.Clear(RewardB);

        Assert.Null(service.SoundOf(RewardA));
        Assert.Null(service.SoundOf(RewardB));
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void Deleted_rewards_lose_their_sounds_and_unknown_ids_are_harmless()
    {
        using var dir = new TempDir();
        var (service, settings, folder) = Make(dir);
        service.Choose(RewardA, Source(dir, "a.mp3"));
        service.Choose(RewardB, Source(dir, "b.mp3"));

        service.Forget(new[] { RewardA, RewardB, "never-had-one", RewardA });

        Assert.Empty(settings.Current.RewardSounds);
        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public void The_single_sound_of_the_first_version_is_cleaned_away_and_the_per_reward_sounds_stay()
    {
        using var dir = new TempDir();
        var folder = dir.File("alerts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "reward.mp3"), "old");
        File.WriteAllText(Path.Combine(folder, $"r-{RewardA}.mp3"), "keep");

        _ = new RewardAlertService(new SettingsStore(dir.File("settings.json")), folder);

        Assert.Equal(new[] { $"r-{RewardA}.mp3" }, Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void The_sounds_survive_a_restart_and_an_old_settings_file_with_the_single_sound_still_loads()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.RewardSounds[RewardA] = new RewardSound { File = $"r-{RewardA}.mp3", Name = "Hype Horn.mp3", Volume = 0.35 };
        settings.Save();

        settings.Current.SoundRewardIds.AddRange(new[] { RewardA, RewardB });
        settings.Save();

        var reloaded = new SettingsStore(dir.File("settings.json")).Current;

        Assert.Equal(new[] { RewardA, RewardB }, reloaded.SoundRewardIds);
        var sound = reloaded.RewardSounds[RewardA];
        Assert.Equal(($"r-{RewardA}.mp3", "Hype Horn.mp3", 0.35), (sound.File, sound.Name, sound.Volume));

        File.WriteAllText(dir.File("old.json"), "{\"RewardAlertFile\":\"reward.mp3\",\"RewardAlertName\":\"x.mp3\",\"RewardAlertVolume\":0.5,\"NotifyRewards\":false}");
        var old = new SettingsStore(dir.File("old.json")).Current;
        Assert.False(old.NotifyRewards);
        Assert.Empty(old.RewardSounds);
        Assert.Empty(old.SoundRewardIds);
    }
}
