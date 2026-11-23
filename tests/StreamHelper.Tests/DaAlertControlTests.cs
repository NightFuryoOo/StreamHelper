using System.Text.Json;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using FakeWidget = StreamHelper.Tests.DaMediaFeedTests.FakeWidget;

namespace StreamHelper.Tests;

public class DaAlertControlTests
{
    private const string Link = "https://www.donationalerts.com/widget/alerts?group_id=1&token=SECRET42";
    private const string Start = """42["alert-show","{\"action\":\"start\",\"duration\":5000,\"group_id\":1,\"alert_id\":194224106,\"alert_type\":\"1\"}"]""";
    private const string End = """42["alert-show","{\"action\":\"end\",\"alert_id\":194224106,\"alert_type\":\"1\"}"]""";
    private const string SkipEcho = """42["alert-show","{\"action\":\"skip\",\"alert_id\":\"194224106\",\"alert_type\":\"1\"}"]""";

    [Fact]
    public void The_start_end_and_skip_messages_of_the_widget_are_read_as_DA_sends_them()
    {
        Assert.Equal(new DaAlertShow("start", 194224106, "1", 5000), DaMediaFeed.AlertShowOf(Start));
        Assert.Equal(new DaAlertShow("end", 194224106, "1", 0), DaMediaFeed.AlertShowOf(End));
        Assert.Equal(new DaAlertShow("skip", 194224106, "1", 0), DaMediaFeed.AlertShowOf(SkipEcho));
        Assert.Equal(new DaAlertShow("start", 7, "4", 0), DaMediaFeed.AlertShowOf("""42["alert-show",{"action":"start","alert_id":"7","alert_type":4}]"""));
    }

    [Theory]
    [InlineData("""42["donation","{\"id\":194224106,\"alert_type\":\"1\"}"]""")]
    [InlineData("""42["media","{\"action\":\"get-current-media\",\"source\":\"last_alerts_widget\"}"]""")]
    [InlineData("""42["alert-show","{\"action\":\"start\"}"]""")]
    [InlineData("""42["alert-show","not json"]""")]
    [InlineData("40")]
    [InlineData("3")]
    public void Other_messages_are_not_taken_for_an_alert(string packet)
    {
        Assert.Null(DaMediaFeed.AlertShowOf(packet));
    }

    [Theory]
    [InlineData("""({"status":"success","message":""})""", true, "")]
    [InlineData("""{"status":"success"}""", true, "")]
    [InlineData("""callback({"status":"error","message":"Alert not found"});""", false, "DA отказал: Alert not found")]
    [InlineData("""({"status":"error"})""", false, "DA отказал.")]
    [InlineData("<html>oops</html>", false, "DA ответил непонятно.")]
    public void The_answer_of_DA_is_read_with_or_without_the_brackets_around_it(string body, bool success, string message)
    {
        Assert.Equal(new DaCommandResult(success, message), DaMediaFeed.ApiAnswer(body));
    }

    [Fact]
    public async Task The_feed_knows_which_alert_is_on_screen_until_it_ends()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var widget = new FakeWidget(async socket =>
        {
            await DaMediaFeedTests.Send(socket, Start);
            await finish.Task;
            await DaMediaFeedTests.Send(socket, End);
        });
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);
        var changes = Changes(feed);
        feed.Start();
        try
        {
            await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(194224106, feed.CurrentAlert?.Id);
            Assert.Equal("1", feed.CurrentAlert?.Type);
            Assert.True(feed.IsShowing(194224106));
            Assert.False(feed.IsShowing(5));

            finish.SetResult();
            await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(feed.CurrentAlert);
            Assert.False(feed.IsShowing(194224106));
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task Skipping_sends_what_the_DA_site_sends_and_waits_for_DA_to_confirm()
    {
        using var widget = new FakeWidget(socket => DaMediaFeedTests.Send(socket, Start));
        widget.OnFrame = (socket, text) => text.Contains("\"skip\"") ? DaMediaFeedTests.Send(socket, SkipEcho) : Task.CompletedTask;
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);
        var changes = Changes(feed);
        feed.Start();
        try
        {
            await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            var result = await feed.SkipCurrentAsync();

            Assert.Equal(new DaCommandResult(true, DaMediaFeed.Skipped), result);
            Assert.Contains("""42["alert-show",{"token":"SECRET42","message_data":{"action":"skip","alert_id":"194224106","alert_type":"1"}}]""", widget.Received);
            Assert.Null(feed.CurrentAlert);
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task A_skip_DA_does_not_confirm_is_reported_and_not_called_done()
    {
        using var widget = new FakeWidget(socket => DaMediaFeedTests.Send(socket, Start));
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site) { SkipConfirmWait = TimeSpan.FromMilliseconds(300) };
        var changes = Changes(feed);
        feed.Start();
        try
        {
            await changes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new DaCommandResult(false, DaMediaFeed.NotConfirmed), await feed.SkipAsync(194224106, "1"));
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task The_skip_key_says_why_nothing_happened()
    {
        Assert.Equal(DaMediaFeed.NoLink, (await new DaMediaFeed(new HttpClient(), () => "").SkipCurrentAsync()).Message);
        Assert.Equal(DaMediaFeed.NoChannel, (await new DaMediaFeed(new HttpClient(), () => Link).SkipCurrentAsync()).Message);

        using var widget = new FakeWidget(_ => Task.CompletedTask);
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.StatusChanged += status =>
        {
            if (status.State == SyncState.Ok) connected.TrySetResult();
        };
        feed.Start();
        try
        {
            await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new DaCommandResult(false, DaMediaFeed.NothingShowing), await feed.SkipCurrentAsync());
            Assert.Empty(widget.Received);
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task Repeating_asks_DA_to_show_that_donation_again_with_the_widget_key()
    {
        using var widget = new FakeWidget(_ => Task.CompletedTask);
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);

        var result = await feed.RepeatAsync(194224106);

        Assert.Equal(new DaCommandResult(true, DaMediaFeed.Repeated), result);
        Assert.Equal("/api/repeatalert?alert=194224106&alert_type=1&token=SECRET42", Assert.Single(widget.ApiAsked));
    }

    [Theory]
    [InlineData(200, """({"status":"error","message":"Alert not found"})""", "DA отказал: Alert not found")]
    [InlineData(500, "", "DA ответил HTTP 500.")]
    [InlineData(403, "<html>An error occurred</html>", DaMediaFeed.RepeatForbidden)]
    public async Task A_refused_repeat_says_why(int status, string body, string message)
    {
        using var widget = new FakeWidget(_ => Task.CompletedTask) { ApiAnswer = _ => (status, body) };
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);

        Assert.Equal(new DaCommandResult(false, message), await feed.RepeatAsync(194224106));
    }

    [Fact]
    public async Task Without_the_widget_link_nothing_is_sent_to_DA()
    {
        using var widget = new FakeWidget(_ => Task.CompletedTask);
        var feed = new DaMediaFeed(new HttpClient(), () => "", widget.Site);

        Assert.Equal(new DaCommandResult(false, DaMediaFeed.NoLink), await feed.RepeatAsync(1));
        Assert.Equal(new DaCommandResult(false, DaMediaFeed.NoLink), await feed.SkipAsync(1, "1"));
        Assert.Empty(widget.ApiAsked);
    }

    [Fact]
    public void Only_DonationAlerts_donations_get_the_alert_buttons_and_the_on_screen_mark_is_not_saved()
    {
        var da = new Donation { Id = 194224106, Username = "viewer", Amount = 200, Currency = "RUB" };
        var other = new Donation { Id = 5, Source = DonationSources.DonatePay, Username = "x" };
        var raised = new List<string?>();
        da.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(da.CanRepeatAlert);
        da.AlertShowing = true;

        Assert.True(da.IsDonationAlerts);
        Assert.False(other.IsDonationAlerts);
        Assert.False(other.CanRepeatAlert);
        Assert.False(da.CanRepeatAlert);
        Assert.Contains(nameof(Donation.AlertShowing), raised);
        Assert.Contains(nameof(Donation.CanRepeatAlert), raised);
        var json = JsonSerializer.Serialize(da);
        Assert.DoesNotContain("AlertShowing", json);
        Assert.DoesNotContain("IsDonationAlerts", json);
        Assert.DoesNotContain("CanRepeatAlert", json);
    }

    [Fact]
    public void The_skip_key_has_a_name()
    {
        Assert.Contains(HotkeyAction.DaSkipAlert, HotkeyActions.All);
        Assert.Equal("DA: пропустить оповещение", HotkeyActions.Title(HotkeyAction.DaSkipAlert));
    }

    private const string Resub = """42["donation","{\"id\":6824252,\"alert_type\":\"4\",\"is_shown\":\"0\",\"additional_data\":\"{\\\"randomness\\\":744,\\\"event_data\\\":{\\\"display_name\\\":\\\"bellriver0\\\",\\\"sub_plan\\\":\\\"1000\\\",\\\"cumulative_months\\\":15,\\\"context\\\":\\\"resub\\\",\\\"is_gift\\\":false,\\\"sub_message\\\":{\\\"message\\\":\\\"драсте друки\\\",\\\"emotes\\\":null}},\\\"months_in_a_row\\\":15}\",\"billing_system\":\"TWITCH\",\"billing_system_type\":\"REGULAR_1000\",\"username\":\"bellriver0\",\"amount\":\"0.00\",\"currency\":\"USD\",\"message\":\"драсте друки\",\"_is_test_alert\":false}"]""";
    private const string ResubShown = """42["donation","{\"id\":6824252,\"alert_type\":4,\"is_shown\":1,\"additional_data\":\"{\\\"event_data\\\":{\\\"display_name\\\":\\\"bellriver0\\\",\\\"cumulative_months\\\":15,\\\"context\\\":\\\"resub\\\",\\\"is_gift\\\":false}}\",\"username\":\"bellriver0\"}"]""";
    private const string Gift = """42["donation","{\"id\":11675807,\"alert_type\":\"13\",\"is_shown\":\"0\",\"additional_data\":\"{\\\"randomness\\\":95,\\\"event_data\\\":{\\\"display_name\\\":\\\"kisumi_ex\\\",\\\"recipient_user_name\\\":\\\"rxntigo\\\",\\\"recipient_display_name\\\":\\\"rxntigo\\\",\\\"sub_message\\\":{\\\"message\\\":\\\"\\\",\\\"emotes\\\":null},\\\"sub_plan\\\":\\\"1000\\\",\\\"context\\\":\\\"subgift\\\",\\\"is_gift\\\":true}}\",\"billing_system\":\"TWITCH\",\"billing_system_type\":\"GIFTED_1000\",\"username\":\"rxntigo\",\"amount\":\"0.00\",\"message\":\"\"}"]""";
    private const string Raid = """42["donation","{\"id\":3996017,\"alert_type\":\"17\",\"is_shown\":\"0\",\"additional_data\":\"{\\\"randomness\\\":894,\\\"event_data\\\":{\\\"username\\\":\\\"Monor22\\\",\\\"viewers\\\":12}}\",\"billing_system\":\"TWITCH\",\"billing_system_type\":\"RAID\",\"username\":\"Monor22\",\"amount\":null}"]""";

    [Fact]
    public void Subs_gifts_and_raids_from_the_widget_channel_are_read_as_DA_sends_them()
    {
        Assert.Equal(new DaAlertEvent(6824252, "4", "bellriver0", "bellriver0", "resub", 15, false, false), DaMediaFeed.AlertEventOf(Resub));
        Assert.Equal(new DaAlertEvent(6824252, "4", "bellriver0", "bellriver0", "resub", 15, false, true), DaMediaFeed.AlertEventOf(ResubShown));
        Assert.Equal(new DaAlertEvent(11675807, "13", "rxntigo", "kisumi_ex", "subgift", 0, true, false), DaMediaFeed.AlertEventOf(Gift));
        Assert.Equal(new DaAlertEvent(3996017, "17", "Monor22", "Monor22", "", 0, false, false), DaMediaFeed.AlertEventOf(Raid));
        Assert.Null(DaMediaFeed.AlertEventOf(Start));
        Assert.Null(DaMediaFeed.AlertEventOf("""42["donation","not json"]"""));
    }

    private static Subscriber Sub(string key, SubscriptionKind kind, string login, int months = 0, int gifts = 0, int minutesAgo = 1, bool anonymous = false) =>
        new() { Key = key, Kind = kind, Login = login, DisplayName = login, Months = months, GiftTotal = gifts, IsAnonymous = anonymous, AtUtc = DateTime.UtcNow.AddMinutes(-minutesAgo) };

    [Fact]
    public void A_resub_alert_finds_the_card_with_the_same_nick_and_months()
    {
        var old = Sub("old", SubscriptionKind.Resub, "bellriver0", months: 14, minutesAgo: 60 * 24 * 30);
        var right = Sub("right", SubscriptionKind.Resub, "BellRiver0", months: 15, minutesAgo: 5);
        var other = Sub("other", SubscriptionKind.Resub, "someone", months: 15);
        var alert = DaMediaFeed.AlertEventOf(Resub)!;

        Assert.Same(right, DaAlertMatch.Find(new[] { old, right, other }, alert));
        Assert.Null(DaAlertMatch.Find(new[] { old, other }, alert));
    }

    [Fact]
    public void A_card_that_already_has_this_alert_is_found_again_and_others_are_not_taken_twice()
    {
        var first = Sub("a", SubscriptionKind.Resub, "bellriver0", months: 15, minutesAgo: 10);
        var second = Sub("b", SubscriptionKind.Resub, "bellriver0", months: 15, minutesAgo: 2);
        second.AttachDaAlert(999, "4");
        var alert = DaMediaFeed.AlertEventOf(Resub)!;

        Assert.Same(first, DaAlertMatch.Find(new[] { first, second }, alert));
        first.AttachDaAlert(alert.Id, alert.Type);
        Assert.Same(first, DaAlertMatch.Find(new[] { second, first }, alert));
    }

    [Fact]
    public void A_new_sub_alert_goes_to_a_new_subscription_not_to_a_resub()
    {
        var alert = new DaAlertEvent(1, "4", "newbie", "Newbie", "sub", 1, false, false);
        var resub = Sub("r", SubscriptionKind.Resub, "newbie", months: 3);
        var fresh = Sub("n", SubscriptionKind.New, "newbie", minutesAgo: 3);

        Assert.Same(fresh, DaAlertMatch.Find(new[] { resub, fresh }, alert));
    }

    [Fact]
    public void A_gift_alert_finds_the_single_gift_of_that_gifter_but_not_a_bundle()
    {
        var alert = DaMediaFeed.AlertEventOf(Gift)!;
        var single = Sub("g1", SubscriptionKind.Gift, "kisumi_ex", gifts: 1);
        var bundle = Sub("g5", SubscriptionKind.Gift, "kisumi_ex", gifts: 5);
        var recipient = Sub("rx", SubscriptionKind.New, "rxntigo");

        Assert.Same(single, DaAlertMatch.Find(new[] { bundle, single, recipient }, alert));
        Assert.Null(DaAlertMatch.Find(new[] { bundle, recipient }, alert));
        var anonymous = Sub("an", SubscriptionKind.Gift, "", gifts: 1, anonymous: true);
        Assert.Same(anonymous, DaAlertMatch.Find(new[] { anonymous }, alert with { Name = "AnAnonymousGifter" }));
    }

    [Fact]
    public void Raids_and_donations_are_not_matched_to_subscription_cards()
    {
        var raid = DaMediaFeed.AlertEventOf(Raid)!;

        Assert.False(DaAlertMatch.Handles(raid));
        Assert.True(DaAlertMatch.Handles(DaMediaFeed.AlertEventOf(Resub)!));
        Assert.True(DaAlertMatch.Handles(DaMediaFeed.AlertEventOf(Gift)!));
        Assert.Null(DaAlertMatch.Find(new[] { Sub("x", SubscriptionKind.New, "Monor22") }, raid));
    }

    [Fact]
    public void The_alert_number_of_a_subscription_is_kept_after_a_restart_and_shows_the_buttons()
    {
        using var dir = new TempDir();
        var store = new SubscriberStore(dir.File("subscribers.json"));
        var subscriber = Sub("k1", SubscriptionKind.Resub, "bellriver0", months: 15);
        store.AddRange(new[] { subscriber });
        Assert.False(subscriber.CanRepeatAlert);

        subscriber.AttachDaAlert(6824252, "4");
        var again = new SubscriberStore(dir.File("subscribers.json")).Items.Single();

        Assert.True(subscriber.CanRepeatAlert);
        Assert.Equal(6824252, again.DaAlertId);
        Assert.Equal("4", again.DaAlertType);
        subscriber.AlertShowing = true;
        Assert.False(subscriber.CanRepeatAlert);
        Assert.DoesNotContain("AlertShowing", JsonSerializer.Serialize(subscriber));
        Assert.DoesNotContain("DaAlert", JsonSerializer.Serialize(Sub("plain", SubscriptionKind.New, "x")));
    }

    [Fact]
    public async Task The_feed_reports_subs_but_keeps_donations_on_the_video_path()
    {
        using var widget = new FakeWidget(async socket =>
        {
            await DaMediaFeedTests.Send(socket, """42["donation","{\"id\":5,\"alert_type\":\"1\",\"is_shown\":\"0\",\"additional_data\":null}"]""");
            await DaMediaFeedTests.Send(socket, Resub);
        });
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);
        var seen = new List<DaAlertEvent>();
        var got = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.AlertSeen += alert =>
        {
            lock (seen) seen.Add(alert);
            got.TrySetResult();
        };
        feed.Start();
        try
        {
            await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            lock (seen) Assert.Equal(new[] { "4" }, seen.Select(a => a.Type));
        }
        finally
        {
            feed.Stop();
        }
    }

    private static System.Threading.Channels.Channel<bool> Changes(DaMediaFeed feed)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        feed.AlertChanged += () => channel.Writer.TryWrite(true);
        return channel;
    }
}
