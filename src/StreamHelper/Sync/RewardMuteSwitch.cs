using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class RewardMuteSwitch
{
    private readonly SettingsStore _settings;
    private readonly IRewardApi _api;
    private bool _running;
    private bool _again;

    public RewardMuteSwitch(SettingsStore settings, IRewardApi api)
    {
        _settings = settings;
        _api = api;
    }

    public string Status { get; private set; } = "";

    public event Action<string>? StatusChanged;

    public event Action<string, bool>? Switched;

    public static IReadOnlyList<string> WithSound(AppSettings settings) =>
        settings.SoundRewardIds
            .Where(id => settings.RewardSounds.TryGetValue(id, out var sound) && !string.IsNullOrWhiteSpace(sound.File))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    public static HashSet<string> Targets(AppSettings settings)
    {
        var managed = settings.ManagedRewardIds.ToHashSet(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in WithSound(settings))
        {
            if (managed.Contains(id)) targets.Add(id);
            if (settings.RewardCopies.TryGetValue(id, out var copy) && managed.Contains(copy)) targets.Add(copy);
        }
        return targets;
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (_running)
        {
            _again = true;
            return;
        }
        _running = true;
        try
        {
            do
            {
                _again = false;
                await SyncOnceAsync(ct);
            }
            while (_again && !ct.IsCancellationRequested);
        }
        finally
        {
            _running = false;
        }
    }

    public void ForgetManual(string rewardId)
    {
        if (_settings.Current.MuteSwitchedOffRewardIds.Remove(rewardId)) _settings.Save();
    }

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var active = settings.RewardSoundsMuted && settings.MuteSwitchesRewardsOff;
        var desired = active ? Targets(settings) : new HashSet<string>(StringComparer.Ordinal);
        var switchedOff = settings.MuteSwitchedOffRewardIds;
        var turnOn = switchedOff.Where(id => !desired.Contains(id)).ToList();
        var turnOff = desired.Where(id => !switchedOff.Contains(id)).ToList();
        var untouchable = active ? WithSound(settings).Where(id => !settings.ManagedRewardIds.Contains(id)).ToList() : new List<string>();
        if (turnOn.Count == 0 && turnOff.Count == 0 && untouchable.Count == 0)
        {
            SetStatus(Summary(settings, Array.Empty<string>()));
            return;
        }
        if (!settings.HasTwitchTokens || !settings.HasManageScope)
        {
            SetStatus(turnOn.Count == 0 && turnOff.Count == 0
                ? ""
                : "Чтобы выключать награды, подключи Twitch во вкладке «Twitch» (нужно право на управление наградами).");
            return;
        }

        var failures = new List<string>();
        var stillOn = new List<string>();
        try
        {
            if (turnOff.Count > 0 || untouchable.Count > 0)
            {
                var rewards = (await _api.GetRewardsAsync(ct)).ToDictionary(r => r.Id, StringComparer.Ordinal);
                foreach (var id in turnOff)
                {
                    if (!rewards.TryGetValue(id, out var reward) || !reward.IsEnabled) continue;
                    var result = await _api.SetRewardEnabledAsync(id, false, ct);
                    if (result.Success)
                    {
                        switchedOff.Add(id);
                        _settings.Save();
                        Switched?.Invoke(id, false);
                    }
                    else if (!result.Missing) failures.Add($"«{Title(reward)}»: {result.Message}");
                }
                stillOn.AddRange(untouchable
                    .Where(id => rewards.TryGetValue(id, out var reward) && reward.IsEnabled)
                    .Select(id => Title(rewards[id])));
            }
            foreach (var id in turnOn)
            {
                var result = await _api.SetRewardEnabledAsync(id, true, ct);
                if (result.Success || result.Missing)
                {
                    switchedOff.Remove(id);
                    _settings.Save();
                    if (result.Success) Switched?.Invoke(id, true);
                }
                else failures.Add(result.Message);
            }
        }
        catch (Exception ex) when (ex is AuthRequiredException or HttpRequestException or TaskCanceledException)
        {
            Log.Write("Switching rewards with the sounds failed: " + ex.Message);
            failures.Add(ex.Message);
        }

        SetStatus(failures.Count == 0
            ? Summary(settings, stillOn)
            : "Не получилось переключить награды: " + string.Join("; ", failures.Distinct()) + " Попробую снова при следующем переключении звуков.");
    }

    public static string Summary(AppSettings settings, IReadOnlyList<string> stillOn)
    {
        if (!settings.RewardSoundsMuted || !settings.MuteSwitchesRewardsOff) return "";
        var lines = new List<string>();
        var off = settings.MuteSwitchedOffRewardIds.Count;
        if (off > 0) lines.Add($"Сейчас выключено на время без звука: {off} {Words.Plural(off, "награда", "награды", "наград")}.");
        if (stillOn.Count > 0)
        {
            lines.Add("Остались включёнными (созданы в Twitch, программе их выключать нельзя): " + string.Join(", ", stillOn.Select(t => "«" + t + "»")) + ".");
        }
        return string.Join("\n", lines);
    }

    private static string Title(RewardInfo reward)
    {
        var title = reward.Title.Replace("​", "");
        return string.IsNullOrWhiteSpace(title) ? "Награда без названия" : title;
    }

    private void SetStatus(string text)
    {
        if (Status == text) return;
        Status = text;
        StatusChanged?.Invoke(text);
    }
}
