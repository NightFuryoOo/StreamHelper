using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record SeenSyncResult(IReadOnlyList<Donation> Items, IReadOnlyList<string> Seen, bool Baselined);

public static class SeenSync
{
    public const int Keep = 300;

    public static SeenSyncResult Apply(IReadOnlyList<Donation> window, IReadOnlyCollection<string> seen, bool baselined)
    {
        var known = seen.ToHashSet();
        var fresh = baselined
            ? window.Where(d => !known.Contains(d.Key)).GroupBy(d => d.Key).Select(g => g.First()).OrderBy(d => d.Id).ToList()
            : new List<Donation>();
        var next = window.OrderByDescending(d => d.Id).Select(d => d.Key).Concat(seen).Distinct().Take(Keep).ToList();
        return new SeenSyncResult(fresh, next, true);
    }
}

public abstract class WindowDonationPoller : PollingService
{
    private readonly SettingsStore _settings;
    private readonly IDonationWindowSource _source;
    private readonly Func<IReadOnlyList<Donation>, Task> _deliver;
    private string? _rejectedKey;

    protected WindowDonationPoller(SettingsStore settings, IDonationWindowSource source, Func<IReadOnlyList<Donation>, Task> deliver)
    {
        _settings = settings;
        _source = source;
        _deliver = deliver;
    }

    protected abstract string ReadKey(AppSettings settings);

    protected abstract string NotConnectedMessage { get; }

    protected abstract (List<string> Seen, bool Baselined) ReadCursor(AppSettings settings);

    protected abstract void WriteCursor(AppSettings settings, List<string> seen, bool baselined);

    public void Retry()
    {
        _rejectedKey = null;
        PollSoon();
    }

    internal override async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var settings = _settings.Current;
        var key = ReadKey(settings);
        if (key.Length == 0)
        {
            _rejectedKey = null;
            SetStatus(SyncState.NotConnected, NotConnectedMessage);
            return false;
        }
        if (key == _rejectedKey) return false;

        IReadOnlyList<Donation> window;
        try
        {
            window = await _source.GetRecentAsync(ct);
        }
        catch (AuthRequiredException ex)
        {
            _rejectedKey = key;
            SetStatus(SyncState.NeedsLogin, ex.Message);
            return false;
        }
        _rejectedKey = null;
        var (seen, baselined) = ReadCursor(settings);
        var result = SeenSync.Apply(window, seen, baselined);
        if (result.Items.Count > 0)
        {
            await _deliver(result.Items);
        }
        if (baselined != result.Baselined || !seen.SequenceEqual(result.Seen))
        {
            WriteCursor(settings, result.Seen.ToList(), result.Baselined);
            _settings.Save();
        }
        SetStatus(SyncState.Ok, "Подключено");
        return true;
    }
}

public sealed class DonatePayPoller : WindowDonationPoller
{
    public DonatePayPoller(SettingsStore settings, IDonationWindowSource source, Func<IReadOnlyList<Donation>, Task> deliver)
        : base(settings, source, deliver)
    {
    }

    protected override string Name => "DonatePay";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(20);

    protected override string NotConnectedMessage => "Не подключено";

    protected override string ReadKey(AppSettings settings) => settings.DonatePayKey;

    protected override (List<string> Seen, bool Baselined) ReadCursor(AppSettings settings) =>
        (settings.DonatePaySeen, settings.DonatePayBaselined);

    protected override void WriteCursor(AppSettings settings, List<string> seen, bool baselined)
    {
        settings.DonatePaySeen = seen;
        settings.DonatePayBaselined = baselined;
    }
}

public sealed class DonateXPoller : WindowDonationPoller
{
    public DonateXPoller(SettingsStore settings, IDonationWindowSource source, Func<IReadOnlyList<Donation>, Task> deliver)
        : base(settings, source, deliver)
    {
    }

    protected override string Name => "DonateX";

    protected override string NotConnectedMessage => "Не подключено";

    protected override string ReadKey(AppSettings settings) => settings.DonateXToken;

    protected override (List<string> Seen, bool Baselined) ReadCursor(AppSettings settings) =>
        (settings.DonateXSeen, settings.DonateXBaselined);

    protected override void WriteCursor(AppSettings settings, List<string> seen, bool baselined)
    {
        settings.DonateXSeen = seen;
        settings.DonateXBaselined = baselined;
    }
}
