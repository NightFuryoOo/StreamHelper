using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class DaAlertPauseTests : IDisposable
{
    private const string Link = "https://www.donationalerts.com/widget/alerts?group_id=1&token=SECRET42";
    private const string Gift = DaMediaFeed.GiftAlertType;
    private const string Sub = DaMediaFeed.SubAlertType;
    private const string Donation = DaMediaFeed.DonationAlertType;
    private static readonly DateTime T0 = new(2026, 11, 18, 18, 0, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sh-da-pause-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = T0;

    public DaAlertPauseTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private string StateFile => Path.Combine(_folder, DaAlertPause.FileName);

    private DaAlertPause Pause(FakeChannel channel) => new(channel, StateFile, () => _now) { StartWait = TimeSpan.FromSeconds(3) };

    private static async Task Until(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Only_subs_and_gift_subs_are_paused()
    {
        Assert.Equal(new[] { "13", "4" }, DaAlertPause.PausedTypes.OrderBy(t => t));
    }

    [Fact]
    public void Pausing_in_the_middle_of_a_gift_train_takes_the_gift_on_screen_and_the_waiting_ones_off_the_widget()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        channel.Came(100, Donation);
        channel.Show("start", 100, Donation);
        channel.Show("end", 100, Donation);
        for (var id = 1; id <= 5; id++) channel.Came(id);
        channel.Came(200, Donation);
        channel.Came(100, Donation, shown: true);
        channel.Show("start", 1);
        channel.Show("end", 1);
        channel.Show("start", 2);

        var result = pause.Toggle();

        Assert.True(result.Success);
        Assert.Equal("Подписки DA на паузе. Ждут: 4.", result.Message);
        Assert.Equal(DaPauseMode.Paused, pause.Mode);
        Assert.Equal(new long[] { 2, 3, 4, 5 }, pause.Queue.Select(q => q.Id));
        Assert.Equal(new long[] { 2, 3, 4, 5 }, channel.Skipped.Select(s => s.Id));
        Assert.Equal("Продолжить подписки DA (ждут: 4)", pause.MenuText);
    }

    [Fact]
    public void While_paused_a_sub_is_skipped_the_moment_it_comes_and_waits_in_order_but_donations_go_on()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();

        channel.Came(11);
        Assert.Equal(new[] { (11L, Gift) }, channel.Skipped);
        channel.Came(12, Sub);
        channel.Came(13, Donation);
        channel.Came(14, "17");
        channel.Came(15, "6");
        channel.Came(12, Sub, shown: true);

        Assert.Equal(new[] { (11L, Gift), (12L, Sub) }, channel.Skipped);
        Assert.Equal(new[] { (11L, Gift), (12L, Sub) }, pause.Queue.Select(q => (q.Id, q.Type)));
    }

    [Fact]
    public void The_echo_of_its_own_skip_keeps_the_sub_waiting_but_a_skip_from_the_DA_site_drops_it()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();
        channel.Came(21);
        channel.Came(22);

        channel.Show("skip", 21);
        channel.Show("skip", 22);
        Assert.Equal(new long[] { 21, 22 }, pause.Queue.Select(q => q.Id));

        channel.Show("skip", 22);
        Assert.Equal(new long[] { 21 }, pause.Queue.Select(q => q.Id));
    }

    [Fact]
    public void A_sub_the_widget_started_anyway_is_skipped_at_once_and_kept()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();
        channel.Came(31);

        channel.Show("start", 32, Sub);
        channel.Show("start", 33, Donation);

        Assert.Equal(new[] { (31L, Gift), (32L, Sub) }, channel.Skipped);
        Assert.Equal(new long[] { 32, 31 }, pause.Queue.Select(q => q.Id));
    }

    [Fact]
    public void A_sub_the_same_DA_site_repeat_sends_again_is_skipped_again_but_waits_once()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();
        channel.Came(41);
        channel.Show("skip", 41);
        channel.Came(41);

        Assert.Equal(new[] { (41L, Gift), (41L, Gift) }, channel.Skipped);
        Assert.Single(pause.Queue);
    }

    [Fact]
    public void Old_alerts_the_widget_never_showed_are_not_brought_back()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        channel.Came(1);
        _now = T0 + DaAlertPause.Recent + TimeSpan.FromMinutes(1);
        channel.Came(2);

        pause.Toggle();

        Assert.Equal(new long[] { 2 }, pause.Queue.Select(q => q.Id));
    }

    [Fact]
    public async Task Resuming_repeats_the_waiting_subs_one_by_one()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();
        for (var id = 51; id <= 53; id++) channel.Came(id);
        channel.OnRepeat = (id, type) => Task.Run(async () =>
        {
            await Task.Delay(30);
            channel.Came(id, type);
            channel.Show("start", id, type);
        });

        var result = pause.Toggle();

        Assert.True(result.Success);
        Assert.Equal("Подписки DA снова идут. Ждавшие покажу по очереди: 3.", result.Message);
        await Until(() => pause.Mode == DaPauseMode.Off, "the queue to finish");
        Assert.Equal(new long[] { 51, 52, 53 }, channel.Repeated.Select(r => r.Id));
        Assert.Equal(new long[] { 51, 52, 53 }, channel.Skipped.Select(s => s.Id));
        Assert.Empty(pause.Queue);
        Assert.False(File.Exists(StateFile));
        Assert.Equal("Пауза подписок DA", pause.MenuText);
    }

    [Fact]
    public async Task The_next_sub_is_repeated_only_after_the_previous_one_started()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();
        channel.Came(61);
        channel.Came(62);

        pause.Toggle();
        await Until(() => channel.Repeated.Count == 1, "the first repeat");
        await Task.Delay(300);
        Assert.Single(channel.Repeated);

        channel.Came(61);
        channel.Show("start", 61);
        await Until(() => channel.Repeated.Count == 2, "the second repeat");
        Assert.Equal(new long[] { 62 }, pause.Queue.Select(q => q.Id));
        channel.Show("skip", 62);
        await Until(() => pause.Mode == DaPauseMode.Off, "the queue to finish");
        Assert.Equal(2, channel.Skipped.Count);
    }

    [Fact]
    public async Task When_DA_refuses_a_repeat_the_rest_is_kept_and_the_reason_is_told()
    {
        var channel = new FakeChannel { Answer = (_, _) => new DaCommandResult(false, DaMediaFeed.RepeatForbidden) };
        var pause = Pause(channel);
        string? failed = null;
        pause.Failed += message => failed = message;
        pause.Toggle();
        channel.Came(71);
        channel.Came(72);

        pause.Toggle();
        await Until(() => failed != null, "the failure");

        Assert.Equal("Не получилось продолжить подписки DA. " + DaMediaFeed.RepeatForbidden + " Не показано: 2.", failed);
        Assert.Equal(DaPauseMode.Off, pause.Mode);
        Assert.Equal(new long[] { 71, 72 }, pause.Queue.Select(q => q.Id));

        channel.Answer = (_, _) => new DaCommandResult(true, "");
        channel.OnRepeat = (id, type) => channel.Show("start", id, type);
        Assert.Equal("Подписки DA на паузе. Ждут: 2.", pause.Toggle().Message);
        pause.Toggle();
        await Until(() => pause.Mode == DaPauseMode.Off && pause.Queue.Count == 0, "the second try");
        Assert.Equal(new long[] { 71, 71, 72 }, channel.Repeated.Select(r => r.Id));
    }

    [Fact]
    public async Task Pausing_again_while_the_queue_plays_takes_the_repeated_ones_back_off_the_widget()
    {
        var channel = new FakeChannel { EchoSkips = true };
        var pause = Pause(channel);
        pause.Toggle();
        for (var id = 81; id <= 84; id++) channel.Came(id);
        channel.OnRepeat = (id, type) => channel.Came(id, type);

        pause.Toggle();
        await Until(() => channel.Repeated.Count == 1, "the first repeat");
        channel.Show("start", 81);
        await Until(() => channel.Repeated.Count == 2, "the second repeat");
        var before = channel.Skipped.Count;

        var again = pause.Toggle();

        Assert.Equal("Подписки DA на паузе. Ждут: 4.", again.Message);
        Assert.Equal(new long[] { 81, 82, 83, 84 }, pause.Queue.Select(q => q.Id));
        Assert.Equal(new long[] { 81, 82 }, channel.Skipped.Skip(before).Select(s => s.Id));
        await Task.Delay(200);
        Assert.Equal(2, channel.Repeated.Count);
    }

    [Fact]
    public void Without_the_DA_widget_channel_the_pause_is_refused()
    {
        var channel = new FakeChannel { IsConnected = false };
        var pause = Pause(channel);
        channel.Came(91);

        var result = pause.Toggle();

        Assert.False(result.Success);
        Assert.Equal(DaAlertPause.NoChannel, result.Message);
        Assert.Equal(DaPauseMode.Off, pause.Mode);
        Assert.Empty(channel.Skipped);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public async Task The_pause_survives_a_restart_of_the_program()
    {
        var first = new FakeChannel();
        Pause(first).Toggle();
        first.Came(101);
        first.Came(102);

        var channel = new FakeChannel();
        var again = Pause(channel);

        Assert.Equal(DaPauseMode.Paused, again.Mode);
        Assert.Equal("Подписки DA всё ещё на паузе с прошлого запуска. Ждут: 2.", again.StartupNotice);
        channel.Came(103);
        Assert.Equal(new[] { (103L, Gift) }, channel.Skipped);
        Assert.Equal(new long[] { 101, 102, 103 }, again.Queue.Select(q => q.Id));

        channel.OnRepeat = (id, type) => channel.Show("start", id, type);
        again.Toggle();
        await Until(() => again.Mode == DaPauseMode.Off, "the queue to finish");
        Assert.Equal(new long[] { 101, 102, 103 }, channel.Repeated.Select(r => r.Id));
        Assert.Null(again.StartupNotice);
    }

    [Fact]
    public void Resuming_with_nothing_waiting_just_lets_subs_through_again()
    {
        var channel = new FakeChannel();
        var pause = Pause(channel);
        pause.Toggle();

        var result = pause.Toggle();
        channel.Came(111);

        Assert.Equal("Подписки DA снова идут.", result.Message);
        Assert.Equal(DaPauseMode.Off, pause.Mode);
        Assert.Empty(channel.Skipped);
        Assert.False(File.Exists(StateFile));
    }

    [Fact]
    public async Task The_DA_feed_reports_every_alert_and_every_start_and_end()
    {
        var came = new List<DaAlertEvent>();
        var shown = new List<DaAlertShow>();
        using var widget = new DaMediaFeedTests.FakeWidget(async socket =>
        {
            await DaMediaFeedTests.Send(socket, """42["donation","{\"id\":194224106,\"alert_type\":\"1\",\"is_shown\":\"0\",\"username\":\"Anna\"}"]""");
            await DaMediaFeedTests.Send(socket, """42["donation","{\"id\":11675807,\"alert_type\":\"13\",\"is_shown\":\"0\",\"additional_data\":\"{\\\"event_data\\\":{\\\"display_name\\\":\\\"kisumi_ex\\\"}}\"}"]""");
            await DaMediaFeedTests.Send(socket, """42["alert-show","{\"action\":\"start\",\"duration\":5000,\"group_id\":2,\"alert_id\":11675807,\"alert_type\":\"13\"}"]""");
        });
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site);
        feed.AlertCame += alert => { lock (came) came.Add(alert); };
        feed.AlertShown += show => { lock (shown) shown.Add(show); };
        Assert.False(feed.IsConnected);
        feed.Start();
        try
        {
            await Until(() => { lock (shown) return shown.Count == 1; }, "the start");
            Assert.True(feed.IsConnected);
            lock (came)
            {
                Assert.Equal(new[] { (194224106L, "1"), (11675807L, "13") }, came.Select(c => (c.Id, c.Type)));
                Assert.Equal("kisumi_ex", came[1].Name);
            }
            Assert.Equal(new DaAlertShow("start", 11675807, "13", 5000), shown[0]);
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task The_pause_skips_through_the_real_feed_as_soon_as_a_gift_comes()
    {
        var gift = """42["donation","{\"id\":11675690,\"alert_type\":\"13\",\"is_shown\":\"0\"}"]""";
        var sendGift = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var widget = new DaMediaFeedTests.FakeWidget(async socket =>
        {
            await sendGift.Task;
            await DaMediaFeedTests.Send(socket, gift);
        });
        var feed = new DaMediaFeed(new HttpClient(), () => Link, widget.Site) { SkipConfirmWait = TimeSpan.FromMilliseconds(300) };
        var pause = new DaAlertPause(feed, StateFile, () => _now);
        feed.Start();
        try
        {
            await Until(() => feed.IsConnected, "the channel");
            Assert.True(pause.Toggle().Success);
            sendGift.SetResult();
            await Until(() => widget.Received.Any(r => r.Contains("\"skip\"")), "the skip");
            var skip = widget.Received.First(r => r.Contains("\"skip\""));
            Assert.Contains("\"alert_id\":\"11675690\"", skip);
            Assert.Contains("\"alert_type\":\"13\"", skip);
            Assert.Equal(new long[] { 11675690 }, pause.Queue.Select(q => q.Id));
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public void The_pause_key_is_known()
    {
        Assert.Contains(HotkeyAction.DaPauseSubs, HotkeyActions.All);
        Assert.Equal("DA: пауза подписок", HotkeyActions.Title(HotkeyAction.DaPauseSubs));
    }

    internal sealed class FakeChannel : IDaAlertChannel
    {
        public event Action<DaAlertEvent>? AlertCame;
        public event Action<DaAlertShow>? AlertShown;

        public bool IsConnected { get; set; } = true;

        public List<(long Id, string Type)> Repeated { get; } = new();

        public List<(long Id, string Type)> Skipped { get; } = new();

        public Func<long, string, DaCommandResult> Answer { get; set; } = (_, _) => new DaCommandResult(true, "");

        public Action<long, string>? OnRepeat { get; set; }

        public bool EchoSkips { get; set; }

        public Task<DaCommandResult> SkipAsync(long alertId, string alertType)
        {
            lock (Skipped) Skipped.Add((alertId, alertType));
            if (EchoSkips) AlertShown?.Invoke(new DaAlertShow("skip", alertId, alertType, 0));
            return Task.FromResult(new DaCommandResult(true, DaMediaFeed.Skipped));
        }

        public Task<DaCommandResult> RepeatAsync(long alertId, string alertType)
        {
            lock (Repeated) Repeated.Add((alertId, alertType));
            var answer = Answer(alertId, alertType);
            if (answer.Success) OnRepeat?.Invoke(alertId, alertType);
            return Task.FromResult(answer);
        }

        public void Came(long id, string type = Gift, bool shown = false) =>
            AlertCame?.Invoke(new DaAlertEvent(id, type, "user" + id, "gifter", "subgift", 0, false, shown));

        public void Show(string action, long id, string type = Gift, int ms = 5000) =>
            AlertShown?.Invoke(new DaAlertShow(action, id, type, ms));
    }
}
