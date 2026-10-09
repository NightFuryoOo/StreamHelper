using StreamHelper.Api;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class DonationSyncTests
{
    [Fact]
    public async Task First_run_only_remembers_the_newest_id_and_imports_nothing()
    {
        var source = new FakeSource(new() { [1] = Make.Page(1, 1, 30, 29, 28) });

        var result = await DonationSync.FetchNewAsync(source, 0, baselined: false, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(30, result.LastDonationId);
        Assert.True(result.Baselined);
    }

    [Fact]
    public async Task First_run_with_no_donations_is_baselined_with_zero()
    {
        var source = new FakeSource(new() { [1] = Make.Page(1, 1) });

        var result = await DonationSync.FetchNewAsync(source, 0, baselined: false, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.LastDonationId);
        Assert.True(result.Baselined);
    }

    [Fact]
    public async Task Newest_first_list_returns_only_unseen_donations_oldest_first()
    {
        var source = new FakeSource(new() { [1] = Make.Page(1, 1, 33, 32, 31, 30, 29) });

        var result = await DonationSync.FetchNewAsync(source, 30, baselined: true, CancellationToken.None);

        Assert.Equal(new long[] { 31, 32, 33 }, result.Items.Select(d => d.Id).ToArray());
        Assert.Equal(33, result.LastDonationId);
        Assert.Equal("descending", result.Order);
        Assert.Equal(new[] { 1 }, source.Requested);
    }

    [Fact]
    public async Task Nothing_new_keeps_the_last_id()
    {
        var source = new FakeSource(new() { [1] = Make.Page(1, 1, 30, 29) });

        var result = await DonationSync.FetchNewAsync(source, 30, baselined: true, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(30, result.LastDonationId);
    }

    [Fact]
    public async Task Newest_first_list_walks_to_the_next_page_while_everything_is_new()
    {
        var source = new FakeSource(new()
        {
            [1] = Make.Page(1, 3, 60, 59, 58),
            [2] = Make.Page(2, 3, 57, 56, 55),
            [3] = Make.Page(3, 3, 54, 53, 52),
        });

        var result = await DonationSync.FetchNewAsync(source, 56, baselined: true, CancellationToken.None);

        Assert.Equal(new long[] { 57, 58, 59, 60 }, result.Items.Select(d => d.Id).ToArray());
        Assert.Equal(new[] { 1, 2 }, source.Requested);
    }

    [Fact]
    public async Task Oldest_first_list_is_read_from_the_last_page_backwards()
    {
        var source = new FakeSource(new()
        {
            [1] = Make.Page(1, 3, 1, 2, 3),
            [2] = Make.Page(2, 3, 4, 5, 6),
            [3] = Make.Page(3, 3, 7, 8, 9),
        });

        var result = await DonationSync.FetchNewAsync(source, 5, baselined: true, CancellationToken.None);

        Assert.Equal("ascending", result.Order);
        Assert.Equal(new long[] { 6, 7, 8, 9 }, result.Items.Select(d => d.Id).ToArray());
        Assert.Equal(9, result.LastDonationId);
    }

    [Fact]
    public async Task Oldest_first_list_first_run_baselines_from_the_last_page()
    {
        var source = new FakeSource(new()
        {
            [1] = Make.Page(1, 2, 1, 2, 3),
            [2] = Make.Page(2, 2, 4, 5),
        });

        var result = await DonationSync.FetchNewAsync(source, 0, baselined: false, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(5, result.LastDonationId);
    }

    [Fact]
    public async Task Page_walk_is_capped()
    {
        var pages = new Dictionary<int, DonationPage>();
        for (var i = 1; i <= 50; i++)
        {
            long top = 10_000 - (i - 1) * 3;
            pages[i] = Make.Page(i, 50, top, top - 1, top - 2);
        }
        var source = new FakeSource(pages);

        await DonationSync.FetchNewAsync(source, 0, baselined: true, CancellationToken.None);

        Assert.Equal(DonationSync.MaxPages, source.Requested.Count);
    }

    [Fact]
    public async Task Poller_baselines_first_then_delivers_only_new_donations_and_saves_progress()
    {
        using var dir = new TempDir();
        var settings = Make.Settings(dir);
        var pages = new Dictionary<int, DonationPage> { [1] = Make.Page(1, 1, 10, 9) };
        var source = new FakeSource(pages);
        var delivered = new List<long>();
        var poller = new DonationPoller(settings, source, items =>
        {
            delivered.AddRange(items.Select(d => d.Id));
            return Task.CompletedTask;
        });

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Empty(delivered);
        Assert.Equal(10, settings.Current.LastDonationId);
        Assert.Equal(SyncState.Ok, poller.Status.State);

        pages[1] = Make.Page(1, 1, 12, 11, 10, 9);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new long[] { 11, 12 }, delivered);

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new long[] { 11, 12 }, delivered);

        var reloaded = new StreamHelper.Storage.SettingsStore(dir.File("settings.json"));
        Assert.Equal(12, reloaded.Current.LastDonationId);
        Assert.True(reloaded.Current.Baselined);
    }

    [Fact]
    public async Task Poller_does_not_rewrite_the_settings_file_when_nothing_changed()
    {
        using var dir = new TempDir();
        var settings = Make.Settings(dir);
        var source = new FakeSource(new() { [1] = Make.Page(1, 1, 10, 9) });
        var poller = new DonationPoller(settings, source, _ => Task.CompletedTask);

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(dir.File("settings.json")));
        File.Delete(dir.File("settings.json"));

        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(dir.File("settings.json")));
    }

    [Fact]
    public async Task Poller_does_not_advance_when_delivery_fails()
    {
        using var dir = new TempDir();
        var settings = Make.Settings(dir);
        settings.Current.Baselined = true;
        settings.Current.LastDonationId = 10;
        var source = new FakeSource(new() { [1] = Make.Page(1, 1, 11, 10) });
        var poller = new DonationPoller(settings, source, _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollOnceAsync(CancellationToken.None));

        Assert.Equal(10, settings.Current.LastDonationId);
    }

    [Fact]
    public async Task Poller_reports_not_connected_without_credentials_and_makes_no_request()
    {
        using var dir = new TempDir();
        var settings = new StreamHelper.Storage.SettingsStore(dir.File("settings.json"));
        var source = new FakeSource(new());
        var poller = new DonationPoller(settings, source, _ => Task.CompletedTask);

        await poller.PollOnceAsync(CancellationToken.None);

        Assert.Equal(SyncState.NotConnected, poller.Status.State);
        Assert.Empty(source.Requested);
    }
}
