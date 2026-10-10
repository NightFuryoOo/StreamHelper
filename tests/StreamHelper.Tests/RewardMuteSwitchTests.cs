using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class RewardMuteSwitchTests
{
    private static SettingsStore Ready(TempDir dir)
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "channel:read:redemptions channel:manage:redemptions";
        return settings;
    }

    private static void WithSound(AppSettings settings, string id, string file = "r-x.mp3")
    {
        if (!settings.SoundRewardIds.Contains(id)) settings.SoundRewardIds.Add(id);
        settings.RewardSounds[id] = new RewardSound { File = file, Name = file };
    }

    private static FakeRewardApi Api(params (string Id, bool Enabled)[] rewards)
    {
        var api = new FakeRewardApi();
        foreach (var (id, enabled) in rewards) api.Rewards.Add(new RewardInfo(id, "Награда " + id + "​", 100, enabled));
        return api;
    }

    [Fact]
    public void Targets_are_the_program_rewards_and_copies_of_the_rewards_that_have_a_sound()
    {
        var settings = new AppSettings
        {
            ManagedRewardIds = new List<string> { "own", "copy-1", "copy-2" },
            RewardCopies = new Dictionary<string, string> { ["orig-1"] = "copy-1", ["orig-2"] = "copy-2" },
        };
        WithSound(settings, "own");
        WithSound(settings, "orig-1");
        WithSound(settings, "twitch-only");
        settings.SoundRewardIds.Add("orig-2");
        WithSound(settings, "silent", file: "");

        Assert.Equal(new[] { "copy-1", "own" }, RewardMuteSwitch.Targets(settings).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "own", "orig-1", "twitch-only" }, RewardMuteSwitch.WithSound(settings).ToArray());
    }

    [Fact]
    public async Task Muting_switches_off_the_rewards_with_a_sound_and_unmuting_turns_back_only_those()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "copy-1", "copy-2" };
        settings.Current.RewardCopies = new Dictionary<string, string> { ["orig-1"] = "copy-1", ["orig-2"] = "copy-2" };
        WithSound(settings.Current, "orig-1");
        WithSound(settings.Current, "orig-2");
        var api = Api(("orig-1", false), ("orig-2", false), ("copy-1", true), ("copy-2", false), ("other", true));
        var mute = new RewardMuteSwitch(settings, api);
        var switched = new List<(string, bool)>();
        mute.Switched += (id, on) => switched.Add((id, on));

        settings.Current.RewardSoundsMuted = true;
        await mute.SyncAsync();

        Assert.Equal(new[] { ("copy-1", false) }, api.Switched.ToArray());
        Assert.Equal(new[] { "copy-1" }, settings.Current.MuteSwitchedOffRewardIds.ToArray());
        Assert.Equal("Сейчас выключено на время без звука: 1 награда.", mute.Status);
        Assert.Equal(new[] { "copy-1" }, new SettingsStore(dir.File("settings.json")).Current.MuteSwitchedOffRewardIds.ToArray());

        api.Switched.Clear();
        settings.Current.RewardSoundsMuted = false;
        await mute.SyncAsync();

        Assert.Equal(new[] { ("copy-1", true) }, api.Switched.ToArray());
        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
        Assert.False(api.Rewards.Single(r => r.Id == "copy-2").IsEnabled);
        Assert.Equal(new[] { ("copy-1", false), ("copy-1", true) }, switched.ToArray());
        Assert.Equal("", mute.Status);
    }

    [Fact]
    public async Task Twitch_made_rewards_with_a_sound_that_stay_visible_are_named()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "copy-1" };
        settings.Current.RewardCopies = new Dictionary<string, string> { ["orig-1"] = "copy-1" };
        WithSound(settings.Current, "orig-1");
        WithSound(settings.Current, "orig-9");
        WithSound(settings.Current, "orig-hidden");
        var api = Api(("orig-1", true), ("copy-1", true), ("orig-9", true), ("orig-hidden", false));
        var mute = new RewardMuteSwitch(settings, api);
        settings.Current.RewardSoundsMuted = true;

        await mute.SyncAsync();

        Assert.Equal(new[] { ("copy-1", false) }, api.Switched.ToArray());
        Assert.Equal("Сейчас выключено на время без звука: 1 награда.\n" +
                     "Остались включёнными (созданы в Twitch, программе их выключать нельзя): «Награда orig-1», «Награда orig-9».", mute.Status);
    }

    [Fact]
    public async Task The_switch_can_be_turned_off_and_then_hides_nothing()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "own" };
        WithSound(settings.Current, "own");
        var api = Api(("own", true));
        var mute = new RewardMuteSwitch(settings, api);
        settings.Current.RewardSoundsMuted = true;
        await mute.SyncAsync();

        settings.Current.MuteSwitchesRewardsOff = false;
        await mute.SyncAsync();

        Assert.Equal(new[] { ("own", false), ("own", true) }, api.Switched.ToArray());
        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
        Assert.True(new AppSettings().MuteSwitchesRewardsOff);
    }

    [Fact]
    public async Task A_sound_added_or_removed_while_muted_takes_effect_at_once()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "a", "b" };
        WithSound(settings.Current, "a");
        var api = Api(("a", true), ("b", true));
        var mute = new RewardMuteSwitch(settings, api);
        settings.Current.RewardSoundsMuted = true;
        await mute.SyncAsync();

        WithSound(settings.Current, "b");
        await mute.SyncAsync();
        settings.Current.RewardSounds.Remove("a");
        settings.Current.SoundRewardIds.Remove("a");
        await mute.SyncAsync();

        Assert.Equal(new[] { ("a", false), ("b", false), ("a", true) }, api.Switched.ToArray());
        Assert.Equal(new[] { "b" }, settings.Current.MuteSwitchedOffRewardIds.ToArray());
    }

    [Fact]
    public async Task A_reward_already_switched_off_by_the_streamer_is_left_alone()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "a" };
        WithSound(settings.Current, "a");
        var api = Api(("a", false));
        var mute = new RewardMuteSwitch(settings, api);

        settings.Current.RewardSoundsMuted = true;
        await mute.SyncAsync();
        settings.Current.RewardSoundsMuted = false;
        await mute.SyncAsync();

        Assert.Empty(api.Switched);
    }

    [Fact]
    public async Task A_refusal_is_reported_and_retried_on_the_next_switch()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "a" };
        WithSound(settings.Current, "a");
        var api = Api(("a", true));
        api.OnSwitch = (_, _) => new RewardToggleResult(false, "Twitch разрешает менять эту награду только там, где её создали.");
        var mute = new RewardMuteSwitch(settings, api);
        settings.Current.RewardSoundsMuted = true;

        await mute.SyncAsync();

        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
        Assert.Contains("«Награда a»: Twitch разрешает менять эту награду только там, где её создали.", mute.Status);
        Assert.Contains("Попробую снова", mute.Status);

        api.OnSwitch = null;
        await mute.SyncAsync();
        Assert.Equal(new[] { "a" }, settings.Current.MuteSwitchedOffRewardIds.ToArray());
    }

    [Fact]
    public async Task A_reward_deleted_on_twitch_is_simply_forgotten()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.MuteSwitchedOffRewardIds = new List<string> { "a" };
        var api = Api();
        api.OnSwitch = (_, _) => new RewardToggleResult(false, "Этой награды уже нет на канале.", Missing: true);
        var mute = new RewardMuteSwitch(settings, api);

        await mute.SyncAsync();

        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
        Assert.Equal("", mute.Status);
    }

    [Fact]
    public async Task Without_the_right_it_explains_and_sends_nothing()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.TwitchScopes = "channel:read:redemptions";
        settings.Current.ManagedRewardIds = new List<string> { "a" };
        WithSound(settings.Current, "a");
        var api = Api(("a", true));
        var mute = new RewardMuteSwitch(settings, api);
        settings.Current.RewardSoundsMuted = true;

        await mute.SyncAsync();

        Assert.Empty(api.Switched);
        Assert.Contains("право на управление наградами", mute.Status);
    }

    private sealed class DownApi : IRewardApi
    {
        public Task<IReadOnlyList<RewardInfo>> GetRewardsAsync(CancellationToken ct) => throw new HttpRequestException("network down");
        public Task<IReadOnlySet<string>> GetManageableRewardIdsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DownloadedImage?> DownloadImageAsync(string url, CancellationToken ct) => throw new NotSupportedException();
        public Task<RewardDeleteResult> DeleteRewardAsync(string rewardId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RewardCreateResult> CreateRewardAsync(RewardInfo template, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<RewardToggleResult> SetRewardEnabledAsync(string rewardId, bool enabled, CancellationToken ct) => throw new NotSupportedException();
        public Task<RewardRenameResult> SetRewardTitleAsync(string rewardId, string title, CancellationToken ct) => throw new NotSupportedException();
        public Task<RedemptionUpdateResult> UpdateRedemptionAsync(string rewardId, string redemptionId, RedemptionDecision decision, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task No_connection_is_reported_without_a_crash()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.ManagedRewardIds = new List<string> { "a" };
        WithSound(settings.Current, "a");
        var mute = new RewardMuteSwitch(settings, new DownApi());
        settings.Current.RewardSoundsMuted = true;

        await mute.SyncAsync();

        Assert.Contains("network down", mute.Status);
        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
    }

    [Fact]
    public void A_manual_switch_in_the_table_takes_the_reward_out_of_its_hands()
    {
        using var dir = new TempDir();
        var settings = Ready(dir);
        settings.Current.MuteSwitchedOffRewardIds = new List<string> { "a" };

        new RewardMuteSwitch(settings, Api()).ForgetManual("a");

        Assert.Empty(settings.Current.MuteSwitchedOffRewardIds);
    }

    [Fact]
    public void Deleting_a_reward_drops_it_and_the_settings_live_in_the_right_place()
    {
        using var dir = new TempDir();
        var settings = Ready(dir).Current;
        settings.MuteSwitchedOffRewardIds = new List<string> { "a", "b" };

        RewardCleanup.ApplyLocally(settings, new RedemptionStore(dir.File("r.json")), new[] { "a" });

        Assert.Equal(new[] { "b" }, settings.MuteSwitchedOffRewardIds.ToArray());
        Assert.Contains(nameof(AppSettings.MuteSwitchesRewardsOff), Profile.SharedSettings);
        Assert.Contains(nameof(AppSettings.MuteSwitchedOffRewardIds), Profile.LocalSettings);
    }

    [Fact]
    public void The_summary_counts_with_the_right_word()
    {
        var settings = new AppSettings { RewardSoundsMuted = true, MuteSwitchedOffRewardIds = new List<string> { "a" } };
        Assert.Equal("Сейчас выключено на время без звука: 1 награда.", RewardMuteSwitch.Summary(settings, Array.Empty<string>()));
        settings.MuteSwitchedOffRewardIds.AddRange(new[] { "b", "c", "d", "e" });
        Assert.Equal("Сейчас выключено на время без звука: 5 наград.", RewardMuteSwitch.Summary(settings, Array.Empty<string>()));
        settings.RewardSoundsMuted = false;
        Assert.Equal("", RewardMuteSwitch.Summary(settings, Array.Empty<string>()));
    }
}
