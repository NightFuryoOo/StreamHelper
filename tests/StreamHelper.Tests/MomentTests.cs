using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

internal static class NoticeJson
{
    public static string Event(string messageId, string noticeType, string chatterLogin, string chatterName, string text, string raid = "null", string streak = "null") =>
        "{\"broadcaster_user_id\":\"777\",\"broadcaster_user_login\":\"streamer\",\"broadcaster_user_name\":\"Streamer\"," +
        "\"chatter_user_id\":\"42\",\"chatter_user_login\":\"" + chatterLogin + "\",\"chatter_user_name\":\"" + chatterName + "\",\"chatter_is_anonymous\":false," +
        "\"color\":\"#00FF7F\",\"badges\":[],\"system_message\":\"\",\"message_id\":\"" + messageId + "\"," +
        "\"message\":{\"text\":\"" + text + "\",\"fragments\":[]},\"notice_type\":\"" + noticeType + "\"," +
        "\"sub\":null,\"resub\":null,\"announcement\":null,\"raid\":" + raid + ",\"watch_streak\":" + streak + "}";

    public static string Raid(string messageId, string login, string name, int viewers) =>
        Event(messageId, "raid", login, name, "",
            raid: "{\"user_id\":\"55\",\"user_name\":\"" + name + "\",\"user_login\":\"" + login + "\",\"viewer_count\":" + viewers + ",\"profile_image_url\":\"https://example.com/a.png\"}");

    public static string Streak(string messageId, string login, string name, int count, int points, string text = "") =>
        Event(messageId, "watch_streak", login, name, text,
            streak: "{\"streak_count\":" + count + ",\"channel_points_awarded\":" + points + "}");

    public static EventSubMessage Parse(string eventJson) =>
        EventSubParser.Parse(MockEventSubServer.Notification("n1", EventSubParser.ChatNoticeType, eventJson))!;
}

public class MomentParserTests
{
    [Fact]
    public void A_raid_notice_names_the_raiding_channel_and_its_viewers()
    {
        var message = NoticeJson.Parse(NoticeJson.Raid("nm-1", "big_streamer", "Big_Streamer", 128));

        var moment = message.Moment!;
        Assert.Null(message.Chat);
        Assert.Equal(MomentKind.Raid, moment.Kind);
        Assert.Equal(("nm-1", "big_streamer", "Big_Streamer", 128), (moment.Key, moment.Login, moment.DisplayName, moment.Viewers));
        Assert.Equal(new DateTime(2026, 10, 5, 12, 5, 0, DateTimeKind.Utc), moment.AtUtc, TimeSpan.FromSeconds(1));
        Assert.True(moment.IsRaid);
    }

    [Fact]
    public void A_watch_streak_notice_keeps_the_viewer_the_count_the_points_and_the_message()
    {
        var message = NoticeJson.Parse(NoticeJson.Streak("nm-2", "anna_k", "Anna_K", 5, 450, "Пятый стрим подряд!"));

        var moment = message.Moment!;
        Assert.Equal(MomentKind.Streak, moment.Kind);
        Assert.Equal(("anna_k", "Anna_K", 5, 450), (moment.Login, moment.DisplayName, moment.StreakCount, moment.ChannelPoints));
        Assert.Equal("Пятый стрим подряд!", moment.Message);
        Assert.True(moment.HasMessage);
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("resub")]
    [InlineData("announcement")]
    [InlineData("shared_chat_raid")]
    [InlineData("unknown")]
    public void Other_chat_notices_are_ignored(string noticeType)
    {
        var message = NoticeJson.Parse(NoticeJson.Event("nm-3", noticeType, "viewer", "Viewer", "текст"));

        Assert.Null(message.Moment);
        Assert.Null(message.Chat);
    }

    [Fact]
    public void A_raid_notice_without_its_raid_object_is_ignored()
    {
        Assert.Null(NoticeJson.Parse(NoticeJson.Event("nm-4", "raid", "viewer", "Viewer", "")).Moment);
        Assert.Null(NoticeJson.Parse(NoticeJson.Event("nm-5", "watch_streak", "viewer", "Viewer", "")).Moment);
    }
}

public class MomentTextTests
{
    [Theory]
    [InlineData(1, "привёл 1 зрителя")]
    [InlineData(2, "привёл 2 зрителей")]
    [InlineData(11, "привёл 11 зрителей")]
    [InlineData(21, "привёл 21 зрителя")]
    [InlineData(128, "привёл 128 зрителей")]
    public void A_raid_says_how_many_viewers_came(int viewers, string expected) =>
        Assert.Equal(expected, new ChannelMoment { Kind = MomentKind.Raid, Viewers = viewers }.Detail);

    [Theory]
    [InlineData(1, 0, "серия просмотров: 1 стрим")]
    [InlineData(3, 450, "серия просмотров: 3 стрима · +450 баллов")]
    [InlineData(5, 1001, "серия просмотров: 5 стримов · +1 001 балл")]
    [InlineData(12, 0, "серия просмотров: 12 стримов")]
    public void A_streak_says_how_many_streams_and_the_points(int count, int points, string expected) =>
        Assert.Equal(expected, new ChannelMoment { Kind = MomentKind.Streak, StreakCount = count, ChannelPoints = points }.Detail);

    [Fact]
    public void The_name_falls_back_to_the_login_and_the_kind_has_a_word()
    {
        Assert.Equal("anna_k", new ChannelMoment { Login = "anna_k" }.Name);
        Assert.Equal("рейд", new ChannelMoment { Kind = MomentKind.Raid }.KindText);
        Assert.Equal("стрик", new ChannelMoment { Kind = MomentKind.Streak }.KindText);
    }
}

public class MomentToastTests
{
    private static ChannelMoment Raid(string name, int viewers) => new() { Kind = MomentKind.Raid, DisplayName = name, Viewers = viewers };

    private static ChannelMoment Streak(string name, int count, string text = "") =>
        new() { Kind = MomentKind.Streak, DisplayName = name, StreakCount = count, Message = text };

    [Fact]
    public void One_raid_and_one_streak_have_their_own_titles()
    {
        Assert.Equal(new ToastText("Рейд на канал", "Big · привёл 128 зрителей", null), EventToasts.Moments(new[] { Raid("Big", 128) }));
        Assert.Equal(new ToastText("Серия просмотров", "Anna · 5 стримов подряд", "привет"), EventToasts.Moments(new[] { Streak("Anna", 5, "привет") }));
        Assert.Equal(new ToastText("Серия просмотров", "Mark · 3 стрима подряд", null), EventToasts.Moments(new[] { Streak("Mark", 3) }));
    }

    [Fact]
    public void Several_at_once_are_counted_and_the_newest_is_shown()
    {
        Assert.Equal("Рейдов: 2", EventToasts.Moments(new[] { Raid("A", 1), Raid("B", 2) }).Title);
        Assert.Equal("Стриков: 2", EventToasts.Moments(new[] { Streak("A", 3), Streak("B", 4) }).Title);
        var mixed = EventToasts.Moments(new[] { Raid("A", 10), Streak("B", 4) });
        Assert.Equal("Рейдов и стриков: 2", mixed.Title);
        Assert.Equal("B · 4 стрима подряд", mixed.Line);
    }

    [Fact]
    public void Each_kind_has_its_own_switch()
    {
        var settings = new AppSettings { NotifyRaids = true, NotifyStreaks = false };

        Assert.True(EventToasts.Notifies(settings, Raid("A", 1)));
        Assert.False(EventToasts.Notifies(settings, Streak("B", 3)));
        Assert.True(new AppSettings().NotifyRaids);
        Assert.True(new AppSettings().NotifyStreaks);
    }
}

public class MomentStoreTests
{
    [Fact]
    public void Moments_are_kept_newest_first_once_each_and_seen_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new MomentStore(dir.File("raids.json"));
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

        store.AddRange(new[]
        {
            new ChannelMoment { Key = "b", Kind = MomentKind.Streak, Login = "b", StreakCount = 3, AtUtc = t0.AddMinutes(1) },
            new ChannelMoment { Key = "a", Kind = MomentKind.Raid, Login = "a", Viewers = 7, AtUtc = t0 },
        });
        Assert.Empty(store.AddRange(new[] { new ChannelMoment { Key = "a", Kind = MomentKind.Raid, AtUtc = t0 } }));
        store.Items[1].Seen = true;

        var reloaded = new MomentStore(dir.File("raids.json"));
        Assert.Equal(new[] { "b", "a" }, reloaded.Items.Select(m => m.Key).ToArray());
        Assert.Equal(1, reloaded.UnseenCount);
        Assert.Equal((MomentKind.Raid, 7, true), (reloaded.Items[1].Kind, reloaded.Items[1].Viewers, reloaded.Items[1].Seen));
    }
}

public class MomentViewModelTests
{
    private static MainViewModel NewViewModel(TempDir dir, MomentStore moments, int tab) =>
        new(new DonationStore(dir.File("d.json")), new FollowerStore(dir.File("f.json")), new SubscriberStore(dir.File("s.json")),
            new RedemptionStore(dir.File("r.json")), new PingStore(dir.File("p.json")), moments, tab);

    [Fact]
    public void The_moments_tab_is_the_sixth_and_counts_towards_the_unseen_total()
    {
        using var dir = new TempDir();
        var moments = new MomentStore(dir.File("m.json"));
        var viewModel = NewViewModel(dir, moments, 5);

        Assert.True(viewModel.IsMomentsTab);
        Assert.False(viewModel.IsPingsTab);
        moments.AddRange(new[] { new ChannelMoment { Key = "a", Kind = MomentKind.Raid, AtUtc = DateTime.UtcNow } });
        Assert.Equal(1, viewModel.TotalUnseen);
        Assert.Equal(5, HotkeyActions.TabIndex(HotkeyAction.Moments));
        Assert.Equal("Рейды и стрики", HotkeyActions.Title(HotkeyAction.Moments));
        Assert.False(new AppSettings().GetHotkey(HotkeyAction.Moments).IsSet);
    }

    [Fact]
    public void Ticked_moments_are_deleted_together_and_come_back_with_one_undo()
    {
        using var dir = new TempDir();
        var moments = new MomentStore(dir.File("m.json"));
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        moments.AddRange(new[]
        {
            new ChannelMoment { Key = "a", Kind = MomentKind.Raid, DisplayName = "A", AtUtc = t0 },
            new ChannelMoment { Key = "b", Kind = MomentKind.Streak, DisplayName = "B", AtUtc = t0.AddMinutes(1) },
            new ChannelMoment { Key = "c", Kind = MomentKind.Raid, DisplayName = "C", AtUtc = t0.AddMinutes(2) },
        });
        var viewModel = NewViewModel(dir, moments, 5);

        moments.Items[1].Selected = true;
        viewModel.DeleteSelectedMoments();
        Assert.Equal("Удалено: B (стрик)", viewModel.UndoText);
        viewModel.Undo();

        moments.Items[0].Selected = true;
        moments.Items[2].Selected = true;
        viewModel.DeleteSelectedMoments();
        Assert.Equal(new[] { "b" }, moments.Items.Select(m => m.Key).ToArray());
        Assert.Equal("Удалено записей: 2", viewModel.UndoText);
        viewModel.Undo();
        Assert.Equal(new[] { "c", "b", "a" }, moments.Items.Select(m => m.Key).ToArray());
    }

    [Fact]
    public void The_chat_connection_status_is_worded_for_raids_and_streaks()
    {
        Assert.Equal("Рейды и стрики: timeout", MainViewModel.MomentStatusText(new SyncStatus(SyncState.Error, "Пинги: timeout")));
        Assert.Equal("Рейды и стрики, ошибка: x", MainViewModel.MomentStatusText(new SyncStatus(SyncState.Error, "Пинги, ошибка: x")));
        Assert.Equal("Подключено · streamer", MainViewModel.MomentStatusText(new SyncStatus(SyncState.Ok, "Подключено · streamer")));
    }
}

public class MomentListenerTests
{
    [Fact]
    public async Task Raids_and_streaks_come_through_the_chat_connection_and_are_not_pings()
    {
        using var dir = new TempDir();
        using var server = new MockEventSubServer();
        server.OnConnection = async (_, _, socket) =>
        {
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Welcome("SESS-M"));
            await Wait.Until(() => false, 300);
            var notice = EventSubParser.ChatNoticeType;
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n1", notice, NoticeJson.Raid("nm-1", "big_streamer", "Big_Streamer", 128)));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n2", notice, NoticeJson.Event("nm-2", "sub", "viewer", "Viewer", "@streamer")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n3", notice, NoticeJson.Streak("nm-3", "anna_k", "Anna_K", 5, 450, "@streamer спасибо")));
            await MockEventSubServer.SendAsync(socket, MockEventSubServer.Notification("n4", EventSubParser.ChatMessageType,
                ChatJson.Event("m1", "44", "carol", "Carol", "эй @streamer", ("777", "streamer"))));
            await Task.Delay(Timeout.Infinite);
        };
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = "user:read:chat";
        var api = new FakeEventSubApi();
        var pings = new List<ChatPing>();
        var moments = new List<ChannelMoment>();
        var listener = new PingListener(settings, api, server.Url(), items =>
        {
            lock (pings) pings.AddRange(items);
            return Task.CompletedTask;
        }, onMoment: moment =>
        {
            lock (moments) moments.Add(moment);
        })
        {
            KeepaliveGrace = TimeSpan.FromMilliseconds(400),
            BaseBackoff = TimeSpan.FromMilliseconds(100),
            IdleDelay = TimeSpan.FromMilliseconds(200),
        };

        listener.Start();
        try
        {
            Assert.True(await Wait.Until(() => { lock (pings) return pings.Count >= 1; }));
            await Task.Delay(300);

            Assert.Equal(new[] { EventSubParser.ChatMessageType, EventSubParser.ChatNoticeType }, api.Calls.Select(c => c.Type).ToArray());
            lock (moments) Assert.Equal(new[] { ("nm-1", MomentKind.Raid), ("nm-3", MomentKind.Streak) }, moments.Select(m => (m.Key, m.Kind)).ToArray());
            lock (pings) Assert.Equal(new[] { "m1" }, pings.Select(p => p.Key).ToArray());
        }
        finally
        {
            listener.Stop();
        }
    }
}
