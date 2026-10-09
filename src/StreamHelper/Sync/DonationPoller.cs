using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class DonationPoller : PollingService
{
    private readonly SettingsStore _settings;
    private readonly IDonationSource _source;
    private readonly Func<IReadOnlyList<Donation>, Task> _deliver;
    private bool _orderLogged;

    public DonationPoller(SettingsStore settings, IDonationSource source, Func<IReadOnlyList<Donation>, Task> deliver)
    {
        _settings = settings;
        _source = source;
        _deliver = deliver;
    }

    protected override string Name => "DonationAlerts";

    internal override async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        if (!settings.HasCredentials || !settings.HasTokens)
        {
            SetStatus(SyncState.NotConnected, "Не подключено. Подключи DonationAlerts в настройках, вкладка «Донаты».");
            return false;
        }

        var result = await DonationSync.FetchNewAsync(_source, settings.LastDonationId, settings.Baselined, ct);
        if (!_orderLogged)
        {
            _orderLogged = true;
            Log.Write($"DonationAlerts list order detected: {result.Order}");
        }
        if (result.Items.Count > 0)
        {
            await _deliver(result.Items);
        }
        if (settings.LastDonationId != result.LastDonationId || settings.Baselined != result.Baselined)
        {
            settings.LastDonationId = result.LastDonationId;
            settings.Baselined = result.Baselined;
            _settings.Save();
        }
        SetStatus(SyncState.Ok, "Подключено");
        return true;
    }
}
