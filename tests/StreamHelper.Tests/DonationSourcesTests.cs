using System.Net.Http;
using System.Text.Json;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal sealed class FakeWindowSource : IDonationWindowSource
{
    public List<Donation> Window { get; set; } = new();
    public int Requests { get; private set; }
    public Exception? Fail { get; set; }

    public Task<IReadOnlyList<Donation>> GetRecentAsync(CancellationToken ct)
    {
        Requests++;
        if (Fail != null) throw Fail;
        return Task.FromResult<IReadOnlyList<Donation>>(Window.ToList());
    }
}

public class DonatePayParsingTests
{
    private const string Typical =
        "{\"status\":\"success\",\"count\":2,\"data\":[" +
        "{\"id\":101,\"what\":\"donation\",\"sum\":\"150.50\",\"commission\":\"7.5\",\"status\":\"success\",\"type\":\"donation\"," +
        "\"vars\":{\"name\":\"Иван\",\"comment\":\"Нужен логотип\"}," +
        "\"created_at\":{\"date\":\"2026-10-05 12:00:00.000000\",\"timezone_type\":3,\"timezone\":\"+05:00\"}}," +
        "{\"id\":100,\"sum\":300,\"status\":\"success\",\"type\":\"donation\",\"vars\":{\"name\":\"Анна\"},\"comment\":\"Эмоуты, 5 штук\"," +
        "\"currency\":\"usd\",\"created_at\":\"2026-10-05 11:00:00\"}]}";

    [Fact]
    public void A_transaction_becomes_a_donation_with_name_message_amount_and_utc_time()
    {
        var items = DonatePayClient.ParseTransactions(Typical);

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal(DonationSources.DonatePay, first.Source);
        Assert.Equal(101, first.Id);
        Assert.Equal("Иван", first.Username);
        Assert.Equal("Нужен логотип", first.Message);
        Assert.Equal(150.5m, first.Amount);
        Assert.Equal("RUB", first.Currency);
        Assert.Equal(new DateTime(2026, 10, 5, 7, 0, 0, DateTimeKind.Utc), first.CreatedAtUtc);
        Assert.Equal("DonatePay:101", first.Key);

        var second = items[1];
        Assert.Equal("Эмоуты, 5 штук", second.Message);
        Assert.Equal("USD", second.Currency);
        Assert.Equal(300m, second.Amount);
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc), second.CreatedAtUtc);
    }

    [Fact]
    public void Times_without_a_zone_are_taken_as_moscow_time_and_a_named_zone_is_honoured()
    {
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05 12:00:00", null));
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05 12:00:00.000000", "Mars/Phobos"));
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05 12:00:00.000000", "Europe/Moscow"));
        Assert.Equal(new DateTime(2026, 10, 5, 10, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05 12:00:00", "+02:00"));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05T12:00:00Z", null));
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc), DonatePayClient.ToUtc("2026-10-05T12:00:00+03:00", null));
        Assert.Equal(default, DonatePayClient.ToUtc("not a date", null));
    }

    [Fact]
    public void Cash_outs_unsuccessful_payments_and_rows_without_an_id_are_skipped()
    {
        var items = DonatePayClient.ParseTransactions(
            "{\"status\":\"success\",\"data\":[" +
            "{\"id\":1,\"type\":\"cashout\",\"status\":\"success\",\"sum\":10}," +
            "{\"id\":2,\"type\":\"donation\",\"status\":\"wait\",\"sum\":10}," +
            "{\"type\":\"donation\",\"status\":\"success\",\"sum\":10}," +
            "{\"id\":\"4\",\"type\":\"donation\",\"status\":\"success\",\"sum\":\"10\",\"vars\":{}}]}");

        var only = Assert.Single(items);
        Assert.Equal(4, only.Id);
        Assert.Equal("", only.Username);
    }

    [Fact]
    public void A_refused_key_is_a_login_problem_and_any_other_error_is_a_plain_failure()
    {
        var auth = Assert.Throws<AuthRequiredException>(() =>
            DonatePayClient.ParseTransactions("{\"status\":\"error\",\"message\":\"Incorrect token\"}"));
        Assert.Contains("ключ", auth.Message);

        var other = Assert.Throws<HttpRequestException>(() =>
            DonatePayClient.ParseTransactions("{\"status\":\"error\",\"message\":\"Too many requests\"}"));
        Assert.Contains("Too many requests", other.Message);
    }

    [Fact]
    public void An_empty_or_unexpected_answer_gives_no_donations()
    {
        Assert.Empty(DonatePayClient.ParseTransactions("{\"status\":\"success\",\"data\":[]}"));
        Assert.Empty(DonatePayClient.ParseTransactions("{\"status\":\"success\"}"));
        Assert.Empty(DonatePayClient.ParseTransactions("[]"));
    }
}

public class DonatePayClientTests
{
    private static SettingsStore WithKey(TempDir dir, string key)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.DonatePayKey = key;
        return store;
    }

    [Fact]
    public async Task The_request_asks_for_the_newest_successful_donations_and_escapes_the_key()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (_, _) => (200, "{\"status\":\"success\",\"data\":[]}");
        var client = new DonatePayClient(WithKey(dir, "a b+c&d"), new HttpClient(), server.BaseUrl);

        await client.GetRecentAsync(CancellationToken.None);

        var request = Assert.Single(server.Requests);
        Assert.Contains("GET /api/v1/transactions?access_token=a%20b%2Bc%26d&limit=100&order=DESC&type=donation&status=success", request);
    }

    [Fact]
    public async Task A_refused_key_or_a_busy_service_is_reported_without_the_key_in_the_message()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var client = new DonatePayClient(WithKey(dir, "SECRET-KEY"), new HttpClient(), server.BaseUrl);

        server.Handler = (_, _) => (401, "{\"status\":\"error\",\"message\":\"Incorrect token\"}");
        var refused = await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetRecentAsync(CancellationToken.None));
        Assert.DoesNotContain("SECRET-KEY", refused.Message);

        server.Handler = (_, _) => (200, "{\"status\":\"error\",\"message\":\"Incorrect token\"}");
        await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetRecentAsync(CancellationToken.None));

        server.Handler = (_, _) => (429, "{}");
        var busy = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRecentAsync(CancellationToken.None));
        Assert.DoesNotContain("SECRET-KEY", busy.Message);

        server.Handler = (_, _) => (502, "<html>bad gateway</html>");
        var broken = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRecentAsync(CancellationToken.None));
        Assert.Contains("502", broken.Message);
        Assert.DoesNotContain("SECRET-KEY", broken.Message);
    }

    [Fact]
    public async Task Without_a_key_no_request_is_made_and_an_unreachable_service_does_not_leak_the_key()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var none = new DonatePayClient(new SettingsStore(dir.File("settings.json")), new HttpClient(), server.BaseUrl);
        await Assert.ThrowsAsync<AuthRequiredException>(() => none.GetRecentAsync(CancellationToken.None));
        Assert.Empty(server.Requests);

        var unreachable = new DonatePayClient(WithKey(dir, "SECRET-KEY"), new HttpClient { Timeout = TimeSpan.FromSeconds(3) }, "http://127.0.0.1:1");
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => unreachable.GetRecentAsync(CancellationToken.None));
        Assert.DoesNotContain("SECRET-KEY", failure.Message);
    }
}

public class DonateXTests
{
    private const string Typical =
        "[{\"id\":\"3f2c1d3e-0000-4000-8000-000000000001\",\"username\":\"Мария\",\"message\":\"Баннер для шапки\",\"amount\":250.5,\"currency\":\"RUB\"," +
        "\"amountInRub\":250.5,\"timestamp\":\"2026-10-05T09:00:00.250Z\",\"isTest\":false}," +
        "{\"id\":\"3f2c1d3e-0000-4000-8000-000000000002\",\"username\":\"Tester\",\"message\":\"\",\"amount\":5,\"currency\":\"usd\"," +
        "\"timestamp\":\"2026-10-05T10:00:00Z\",\"isTest\":true}]";

    [Fact]
    public void A_donation_keeps_its_uuid_name_message_amount_and_utc_time()
    {
        var items = DonateXClient.ParseDonations(Typical);

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal(DonationSources.DonateX, first.Source);
        Assert.Equal("3f2c1d3e-0000-4000-8000-000000000001", first.ExternalId);
        Assert.Equal("DonateX:3f2c1d3e-0000-4000-8000-000000000001", first.Key);
        Assert.Equal("Мария", first.Username);
        Assert.Equal("Баннер для шапки", first.Message);
        Assert.Equal(250.5m, first.Amount);
        Assert.Equal("RUB", first.Currency);
        Assert.Equal(new DateTime(2026, 10, 5, 9, 0, 0, 250, DateTimeKind.Utc), first.CreatedAtUtc);
        Assert.True(items[1].Id > first.Id);
    }

    [Fact]
    public void A_test_donation_is_marked_as_a_test_like_the_ones_the_program_makes_itself()
    {
        var items = DonateXClient.ParseDonations(Typical);

        Assert.Equal("[тест]", items[1].Message);
        Assert.Equal("USD", items[1].Currency);
    }

    [Fact]
    public void The_list_may_come_wrapped_and_rows_without_an_id_are_skipped()
    {
        var wrapped = DonateXClient.ParseDonations("{\"data\":[{\"id\":\"a\",\"username\":\"x\",\"amount\":1,\"timestamp\":\"2026-10-05T09:00:00Z\"},{\"username\":\"no id\"}]}");

        var only = Assert.Single(wrapped);
        Assert.Equal("a", only.ExternalId);
        Assert.Equal("RUB", only.Currency);
        Assert.Empty(DonateXClient.ParseDonations("{}"));
        Assert.Empty(DonateXClient.ParseDonations("[]"));
    }

    [Fact]
    public async Task The_request_asks_for_the_newest_donations_with_the_escaped_token()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        server.Handler = (_, _) => (200, "[]");
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.DonateXToken = "to ken/+";
        var client = new DonateXClient(store, new HttpClient(), server.BaseUrl);

        await client.GetRecentAsync(CancellationToken.None);

        Assert.Contains("GET /api/v1/donations?skip=0&take=100&sortOrder=NewestFirst&token=to%20ken%2F%2B", Assert.Single(server.Requests));
    }

    [Fact]
    public async Task A_refused_token_or_a_busy_service_is_reported_without_the_token_in_the_message()
    {
        using var dir = new TempDir();
        using var server = new MockDa();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.DonateXToken = "SECRET-TOKEN";
        var client = new DonateXClient(store, new HttpClient(), server.BaseUrl);

        foreach (var status in new[] { 401, 403 })
        {
            server.Handler = (_, _) => (status, "{}");
            var refused = await Assert.ThrowsAsync<AuthRequiredException>(() => client.GetRecentAsync(CancellationToken.None));
            Assert.DoesNotContain("SECRET-TOKEN", refused.Message);
        }

        server.Handler = (_, _) => (429, "{}");
        var busy = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRecentAsync(CancellationToken.None));
        Assert.Contains("429", busy.Message);

        server.Handler = (_, _) => (500, "{}");
        var broken = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetRecentAsync(CancellationToken.None));
        Assert.DoesNotContain("SECRET-TOKEN", broken.Message);

        var none = new DonateXClient(new SettingsStore(dir.File("other.json")), new HttpClient(), server.BaseUrl);
        var before = server.Requests.Count;
        await Assert.ThrowsAsync<AuthRequiredException>(() => none.GetRecentAsync(CancellationToken.None));
        Assert.Equal(before, server.Requests.Count);
    }
}

public class SeenSyncTests
{
    private static Donation Pay(long id) => new() { Source = DonationSources.DonatePay, Id = id, Username = "u" + id, Amount = 100, Currency = "RUB" };

    [Fact]
    public void The_first_look_remembers_what_is_there_and_delivers_nothing()
    {
        var result = SeenSync.Apply(new[] { Pay(3), Pay(2), Pay(1) }, Array.Empty<string>(), baselined: false);

        Assert.Empty(result.Items);
        Assert.True(result.Baselined);
        Assert.Equal(new[] { "DonatePay:3", "DonatePay:2", "DonatePay:1" }, result.Seen);
    }

    [Fact]
    public void Only_donations_not_seen_before_are_delivered_oldest_first_and_only_once()
    {
        var seen = new[] { "DonatePay:3", "DonatePay:2" };

        var result = SeenSync.Apply(new[] { Pay(5), Pay(4), Pay(3), Pay(2) }, seen, baselined: true);
        Assert.Equal(new long[] { 4, 5 }, result.Items.Select(d => d.Id).ToArray());

        var again = SeenSync.Apply(new[] { Pay(5), Pay(4), Pay(3), Pay(2) }, result.Seen, baselined: true);
        Assert.Empty(again.Items);
    }

    [Fact]
    public void A_donation_confirmed_late_with_a_lower_number_is_still_delivered()
    {
        var first = SeenSync.Apply(new[] { Pay(10), Pay(8) }, Array.Empty<string>(), baselined: false);

        var later = SeenSync.Apply(new[] { Pay(10), Pay(9), Pay(8) }, first.Seen, baselined: true);

        Assert.Equal(9, Assert.Single(later.Items).Id);
    }

    [Fact]
    public void The_memory_is_limited_to_the_newest_keys_and_an_empty_window_forgets_nothing()
    {
        var window = Enumerable.Range(1, 100).Select(i => Pay(1000 + i)).ToList();
        var old = Enumerable.Range(1, 400).Select(i => "DonatePay:" + i).ToList();

        var result = SeenSync.Apply(window, old, baselined: true);

        Assert.Equal(SeenSync.Keep, result.Seen.Count);
        Assert.Equal("DonatePay:1100", result.Seen[0]);
        Assert.Equal(100, result.Seen.Count(k => k.StartsWith("DonatePay:1", StringComparison.Ordinal) && k.Length == 13));

        var empty = SeenSync.Apply(Array.Empty<Donation>(), old.Take(5).ToList(), baselined: true);
        Assert.Empty(empty.Items);
        Assert.Equal(old.Take(5), empty.Seen);
    }
}

public class WindowPollerTests
{
    private static Donation Pay(long id) => new() { Source = DonationSources.DonatePay, Id = id, Username = "u" + id, Amount = 100, Currency = "RUB" };

    [Fact]
    public async Task The_poller_stays_quiet_without_a_key_and_makes_no_request()
    {
        using var dir = new TempDir();
        var source = new FakeWindowSource();
        var poller = new DonatePayPoller(new SettingsStore(dir.File("settings.json")), source, _ => Task.CompletedTask);

        var polled = await poller.PollOnceAsync(CancellationToken.None);

        Assert.False(polled);
        Assert.Equal(0, source.Requests);
        Assert.Equal(SyncState.NotConnected, poller.Status.State);
    }

    [Fact]
    public async Task The_poller_baselines_first_then_delivers_new_donations_and_remembers_them_across_a_restart()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.DonatePayKey = "key";
        var source = new FakeWindowSource { Window = { Pay(2), Pay(1) } };
        var delivered = new List<long>();
        var poller = new DonatePayPoller(settings, source, items =>
        {
            delivered.AddRange(items.Select(d => d.Id));
            return Task.CompletedTask;
        });

        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Empty(delivered);
        Assert.Equal(SyncState.Ok, poller.Status.State);

        source.Window = new List<Donation> { Pay(4), Pay(3), Pay(2), Pay(1) };
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new long[] { 3, 4 }, delivered);

        var restarted = new DonatePayPoller(new SettingsStore(dir.File("settings.json")), source, items =>
        {
            delivered.AddRange(items.Select(d => d.Id));
            return Task.CompletedTask;
        });
        await restarted.PollOnceAsync(CancellationToken.None);
        Assert.Equal(new long[] { 3, 4 }, delivered);
    }

    [Fact]
    public async Task The_poller_does_not_mark_donations_as_seen_when_delivery_fails()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.DonatePayKey = "key";
        settings.Current.DonatePayBaselined = true;
        var source = new FakeWindowSource { Window = { Pay(1) } };
        var poller = new DonatePayPoller(settings, source, _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollOnceAsync(CancellationToken.None));

        Assert.Empty(settings.Current.DonatePaySeen);
    }

    [Fact]
    public async Task The_poller_does_not_rewrite_the_settings_file_when_nothing_changed()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.DonateXToken = "token";
        var source = new FakeWindowSource { Window = { new Donation { Source = DonationSources.DonateX, ExternalId = "a", Id = 5 } } };
        var poller = new DonateXPoller(settings, source, _ => Task.CompletedTask);

        await poller.PollOnceAsync(CancellationToken.None);
        File.Delete(dir.File("settings.json"));
        await poller.PollOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(dir.File("settings.json")));
        Assert.True(settings.Current.DonateXBaselined);
        Assert.Equal(new[] { "DonateX:a" }, settings.Current.DonateXSeen);
    }

    [Fact]
    public async Task A_refused_key_is_not_sent_again_until_another_one_is_pasted_or_the_user_retries()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.DonatePayKey = "wrong";
        var source = new FakeWindowSource { Fail = new AuthRequiredException("ключ отклонён") };
        var poller = new DonatePayPoller(settings, source, _ => Task.CompletedTask);

        Assert.False(await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(new SyncStatus(SyncState.NeedsLogin, "ключ отклонён"), poller.Status);
        Assert.False(await poller.PollOnceAsync(CancellationToken.None));
        Assert.False(await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(1, source.Requests);
        Assert.Equal(SyncState.NeedsLogin, poller.Status.State);

        settings.Current.DonatePayKey = "right";
        source.Fail = null;
        Assert.True(await poller.PollOnceAsync(CancellationToken.None));
        Assert.Equal(2, source.Requests);
        Assert.Equal(SyncState.Ok, poller.Status.State);

        settings.Current.DonatePayKey = "wrong-again";
        source.Fail = new AuthRequiredException("снова отклонён");
        await poller.PollOnceAsync(CancellationToken.None);
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(3, source.Requests);

        poller.Retry();
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(4, source.Requests);
    }

    [Fact]
    public async Task Disconnecting_clears_the_rejection_and_the_status()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        settings.Current.DonateXToken = "wrong";
        var source = new FakeWindowSource { Fail = new AuthRequiredException("токен отклонён") };
        var poller = new DonateXPoller(settings, source, _ => Task.CompletedTask);
        await poller.PollOnceAsync(CancellationToken.None);

        settings.Current.DonateXToken = "";
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(SyncState.NotConnected, poller.Status.State);

        settings.Current.DonateXToken = "wrong";
        await poller.PollOnceAsync(CancellationToken.None);
        Assert.Equal(2, source.Requests);
    }

    [Fact]
    public void DonatePay_is_asked_no_more_than_once_every_twenty_seconds()
    {
        using var dir = new TempDir();
        var settings = new SettingsStore(dir.File("settings.json"));
        var interval = typeof(PollingService).GetProperty("Interval", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var pay = (TimeSpan)interval.GetValue(new DonatePayPoller(settings, new FakeWindowSource(), _ => Task.CompletedTask))!;
        var x = (TimeSpan)interval.GetValue(new DonateXPoller(settings, new FakeWindowSource(), _ => Task.CompletedTask))!;

        Assert.True(pay >= TimeSpan.FromSeconds(20));
        Assert.True(x < pay);
    }
}

public class DonationStatusHubTests
{
    private static SyncStatus Ok(string text = "Подключено") => new(SyncState.Ok, text);
    private static SyncStatus Off => new(SyncState.NotConnected, "Не подключено.");

    [Fact]
    public void With_nothing_connected_the_line_asks_to_connect_one_of_the_three()
    {
        var status = DonationStatusHub.Combine(new[] { ("DonationAlerts", Off), ("DonatePay", Off), ("DonateX", Off) });

        Assert.Equal(SyncState.NotConnected, status.State);
        Assert.Contains("DonatePay", status.Message);
        Assert.Contains("DonateX", status.Message);
    }

    [Fact]
    public void The_line_of_a_user_with_only_DonationAlerts_is_unchanged()
    {
        var status = DonationStatusHub.Combine(new[] { ("DonationAlerts", Ok()), ("DonatePay", Off), ("DonateX", Off) });

        Assert.Equal(Ok(), status);
    }

    [Fact]
    public void A_service_that_is_not_the_first_is_named_and_several_good_ones_are_listed()
    {
        var alone = DonationStatusHub.Combine(new[] { ("DonationAlerts", Off), ("DonatePay", Ok()), ("DonateX", Off) });
        Assert.Equal("DonatePay: Подключено", alone.Message);

        var both = DonationStatusHub.Combine(new[] { ("DonationAlerts", Ok()), ("DonatePay", Off), ("DonateX", Ok()) });
        Assert.Equal(SyncState.Ok, both.State);
        Assert.Equal("Подключено · DonationAlerts, DonateX", both.Message);
    }

    [Fact]
    public void A_message_that_already_starts_with_the_service_is_not_given_its_name_twice()
    {
        var status = DonationStatusHub.Combine(new[]
        {
            ("DonationAlerts", Off), ("DonatePay", new SyncStatus(SyncState.NeedsLogin, "DonatePay отклонил ключ API.")), ("DonateX", Off),
        });

        Assert.Equal("DonatePay отклонил ключ API.", status.Message);
    }

    [Fact]
    public void A_login_problem_beats_an_error_which_beats_a_good_status_and_names_its_service()
    {
        var login = new SyncStatus(SyncState.NeedsLogin, "ключ отклонён");
        var error = new SyncStatus(SyncState.Error, "нет сети");

        var status = DonationStatusHub.Combine(new[] { ("DonationAlerts", Ok()), ("DonatePay", error), ("DonateX", login) });
        Assert.Equal(new SyncStatus(SyncState.NeedsLogin, "DonateX: ключ отклонён"), status);

        var onlyError = DonationStatusHub.Combine(new[] { ("DonationAlerts", Ok()), ("DonatePay", error), ("DonateX", Off) });
        Assert.Equal(new SyncStatus(SyncState.Error, "DonatePay: нет сети"), onlyError);
    }
}

public class DonationFromOtherSourcesTests
{
    [Fact]
    public void Donations_of_different_services_with_the_same_number_are_both_kept()
    {
        using var dir = new TempDir();
        var store = new DonationStore(dir.File("donations.json"));

        store.AddRange(new[] { new Donation { Id = 5, Username = "da", Amount = 1, Currency = "RUB" } });
        store.AddRange(new[] { new Donation { Source = DonationSources.DonatePay, Id = 5, Username = "pay", Amount = 1, Currency = "RUB" } });
        store.AddRange(new[] { new Donation { Source = DonationSources.DonatePay, Id = 5, Username = "pay again", Amount = 1, Currency = "RUB" } });

        Assert.Equal(2, store.Items.Count);
        var reloaded = new DonationStore(dir.File("donations.json"));
        Assert.Equal(new[] { "pay", "da" }, reloaded.Items.Select(d => d.Username).ToArray());
        Assert.Equal(DonationSources.DonatePay, reloaded.Items[0].Source);
    }

    [Fact]
    public void A_file_saved_before_the_other_services_existed_still_loads_as_DonationAlerts()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("donations.json"),
            "[{\"Id\":7,\"Username\":\"old\",\"Amount\":100,\"Currency\":\"RUB\",\"Message\":\"\",\"CreatedAtUtc\":\"2026-10-01T10:00:00Z\",\"Seen\":true,\"Done\":false,\"CustomName\":null}]");

        var store = new DonationStore(dir.File("donations.json"));

        var donation = Assert.Single(store.Items);
        Assert.Equal("7", donation.Key);
        Assert.Equal(DonationSources.DonationAlerts, donation.SourceName);
        Assert.Empty(store.AddRange(new[] { new Donation { Id = 7 } }));
    }

    [Fact]
    public void The_card_says_where_a_donation_came_from_unless_it_is_DonationAlerts()
    {
        var at = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        var da = new Donation { Id = 1, Username = "viewer", CreatedAtUtc = at };
        var pay = new Donation { Source = DonationSources.DonatePay, Id = 1, Username = "viewer", CreatedAtUtc = at };

        Assert.Equal(da.TimeText, da.SubText);
        Assert.Equal($"{pay.TimeText} · DonatePay", pay.SubText);

        da.Rename("Друг");
        pay.Rename("Друг");
        Assert.Equal($"{da.TimeText} · в DonationAlerts: viewer", da.SubText);
        Assert.Equal($"{pay.TimeText} · в DonatePay: viewer", pay.SubText);
    }

    [Fact]
    public void Keys_are_saved_protected_and_a_connected_service_counts_as_a_source()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var store = new SettingsStore(path);
        Assert.False(store.Current.HasAnyDonationSource);

        store.Current.DonatePayKey = "PLAIN-PAY-KEY";
        store.Current.DonateXToken = "PLAIN-X-TOKEN";
        store.Save();

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("PLAIN-PAY-KEY", text);
        Assert.DoesNotContain("PLAIN-X-TOKEN", text);
        var reloaded = new SettingsStore(path);
        Assert.Equal("PLAIN-PAY-KEY", reloaded.Current.DonatePayKey);
        Assert.Equal("PLAIN-X-TOKEN", reloaded.Current.DonateXToken);
        Assert.True(reloaded.Current.HasDonatePayKey);
        Assert.True(reloaded.Current.HasAnyDonationSource);

        reloaded.Current.DonatePayKey = "";
        reloaded.Current.DonateXToken = "";
        Assert.False(reloaded.Current.HasAnyDonationSource);
    }
}
