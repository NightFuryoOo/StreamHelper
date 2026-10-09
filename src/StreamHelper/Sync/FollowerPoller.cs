using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class FollowerPoller : PollingService
{
    private readonly SettingsStore _settings;
    private readonly IFollowerSource _source;
    private readonly Func<IReadOnlyList<Follower>, Task> _deliver;

    public FollowerPoller(SettingsStore settings, IFollowerSource source, Func<IReadOnlyList<Follower>, Task> deliver)
    {
        _settings = settings;
        _source = source;
        _deliver = deliver;
    }

    protected override string Name => "Twitch";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(12);

    internal override async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        if (!settings.HasTwitchCredentials || !settings.HasTwitchTokens)
        {
            SetStatus(SyncState.NotConnected, "Фолловеры: Twitch не подключён. Подключи аккаунт в настройках, вкладка «Twitch».");
            return false;
        }

        var result = await FollowerSync.FetchNewAsync(
            _source, settings.LastFollowerAtUtc, settings.LastFollowerUserIds, settings.FollowersBaselined, ct);
        if (result.Items.Count > 0)
        {
            await _deliver(result.Items);
        }

        var changed = settings.LastFollowerAtUtc != result.LastFollowerAtUtc ||
                      settings.FollowersBaselined != result.Baselined ||
                      !settings.LastFollowerUserIds.SequenceEqual(result.LastUserIds);
        if (changed)
        {
            settings.LastFollowerAtUtc = result.LastFollowerAtUtc;
            settings.FollowersBaselined = result.Baselined;
            settings.LastFollowerUserIds = result.LastUserIds.ToList();
            _settings.Save();
        }
        SetStatus(SyncState.Ok, $"Подключено · {settings.TwitchLogin}");
        return true;
    }
}
