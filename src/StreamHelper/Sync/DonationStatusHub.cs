using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Sync;

public sealed class DonationStatusHub
{
    private readonly (string Name, PollingService Poller)[] _sources;

    public DonationStatusHub(params (string Name, PollingService Poller)[] sources)
    {
        _sources = sources;
        foreach (var source in sources) source.Poller.StatusChanged += _ => Changed?.Invoke(Combined);
    }

    public event Action<SyncStatus>? Changed;

    public SyncStatus Combined => Combine(_sources.Select(s => (s.Name, s.Poller.Status)).ToList());

    public static SyncStatus Combine(IReadOnlyList<(string Name, SyncStatus Status)> sources)
    {
        var active = sources.Where(s => s.Status.State != SyncState.NotConnected).ToList();
        if (active.Count == 0)
        {
            return sources.Count == 1
                ? sources[0].Status
                : new SyncStatus(SyncState.NotConnected, "Не подключено. Подключи DonationAlerts, DonatePay или DonateX в настройках, вкладка «Донаты».");
        }

        bool NamesNeeded(string name) => active.Count > 1 || name != sources[0].Name;
        SyncStatus Labelled((string Name, SyncStatus Status) source) =>
            NamesNeeded(source.Name) && !source.Status.Message.StartsWith(source.Name, StringComparison.OrdinalIgnoreCase)
                ? source.Status with { Message = $"{source.Name}: {source.Status.Message}" }
                : source.Status;

        var needsLogin = active.FirstOrDefault(s => s.Status.State == SyncState.NeedsLogin);
        if (needsLogin.Name != null) return Labelled(needsLogin);
        var failing = active.FirstOrDefault(s => s.Status.State == SyncState.Error);
        if (failing.Name != null) return Labelled(failing);
        if (active.Count == 1) return Labelled(active[0]);
        return new SyncStatus(SyncState.Ok, $"Подключено · {string.Join(", ", active.Select(s => s.Name))}");
    }
}
