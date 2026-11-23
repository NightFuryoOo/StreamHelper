using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class ArchiveTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 17, 0, 0, DateTimeKind.Utc);

    private sealed class FakeLive : ILiveStreamApi
    {
        public LiveStream? Stream;
        public Exception? Failure;
        public int Calls;

        public Task<IReadOnlyList<LiveStream>> GetLiveStreamsAsync(IReadOnlyList<string> userIds, CancellationToken ct)
        {
            Calls++;
            if (Failure != null) throw Failure;
            return Task.FromResult<IReadOnlyList<LiveStream>>(Stream == null ? Array.Empty<LiveStream>() : new[] { Stream });
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeLive Live = new();
        public readonly SettingsStore Settings;
        public readonly ChatFeed Feed = new();
        public readonly DonationStore Donations;
        public readonly SubscriberStore Subscribers;
        public readonly RedemptionStore Redemptions;
        public readonly MomentStore Moments;
        public DateTime Now = T0;
        public ChatArchive Archive;
        public ArchiveRecorder Recorder;

        public Rig()
        {
            Settings = MakeFollower.Settings(Dir);
            Donations = new DonationStore(Dir.File("donations.json"));
            Subscribers = new SubscriberStore(Dir.File("subscribers.json"));
            Redemptions = new RedemptionStore(Dir.File("redemptions.json"));
            Moments = new MomentStore(Dir.File("raids.json"));
            Archive = ChatArchive.InDataFolder(Dir.Path);
            Recorder = NewRecorder();
        }

        private ArchiveRecorder NewRecorder() =>
            new(Archive, Live, Settings, Feed, Donations, Subscribers, Redemptions, Moments, () => Now);

        public void Restart()
        {
            Archive.Dispose();
            Archive = ChatArchive.InDataFolder(Dir.Path);
            Recorder = NewRecorder();
        }

        public Task CheckAsync() => Recorder.CheckAsync(CancellationToken.None);

        public void GoLive(DateTime startedUtc, string title = "Стрим", string game = "Hollow Knight") =>
            Live.Stream = new LiveStream("777", "streamer", game, title, 10, startedUtc);

        public void GoOffline() => Live.Stream = null;

        public void Dispose()
        {
            Archive.Dispose();
            Dir.Dispose();
        }
    }

    private static ChatMessage Chat(string id, DateTime at, string text = "привет", string userId = "u1", string login = "anna") =>
        new(id, userId, login, login.ToUpperInvariant(), text, Array.Empty<ChatMention>(), at);

    [Fact]
    public async Task Nothing_is_recorded_while_the_stream_is_offline()
    {
        using var rig = new Rig();

        await rig.CheckAsync();
        rig.Feed.Push(Chat("m1", T0));
        rig.Donations.AddRange(new[] { new Donation { Id = 5, Username = "Ivan", Amount = 100, Currency = "RUB", CreatedAtUtc = T0 } });

        Assert.Null(rig.Archive.Open);
        Assert.Empty(rig.Archive.Streams());
        Assert.False(Directory.Exists(Path.Combine(rig.Dir.Path, ChatArchive.FolderName)) &&
                     Directory.GetFiles(Path.Combine(rig.Dir.Path, ChatArchive.FolderName), "*.jsonl").Length > 0);
    }

    [Fact]
    public async Task Starting_mid_stream_takes_the_real_start_and_what_was_already_seen_since_then()
    {
        using var rig = new Rig();
        var start = T0.AddMinutes(-30);
        rig.Feed.Push(Chat("before", start.AddMinutes(-5)));
        rig.Feed.Push(Chat("after", start.AddMinutes(5)));
        rig.Donations.AddRange(new[]
        {
            new Donation { Id = 1, Username = "Old", Amount = 50, Currency = "RUB", CreatedAtUtc = start.AddHours(-3) },
            new Donation { Id = 2, Username = "Ivan", Amount = 500, Currency = "RUB", CreatedAtUtc = start.AddMinutes(10) },
        });
        rig.GoLive(start, "Пантеоны", "Hollow Knight");

        await rig.CheckAsync();

        var stream = Assert.Single(rig.Archive.Streams());
        Assert.Equal(start, stream.StartUtc);
        Assert.Equal("Пантеоны", stream.Title);
        Assert.Equal("Hollow Knight", stream.Game);
        var content = rig.Archive.Read(stream.Id);
        Assert.Equal(new[] { "after" }, content.Messages.Select(m => m.MessageId));
        Assert.Equal(new[] { "Ivan" }, content.Donations.Select(d => d.Username));
        Assert.Equal(1, stream.Messages);
        Assert.Equal(1, stream.Donations);
    }

    [Fact]
    public async Task Chat_and_events_during_the_stream_go_to_the_archive_but_test_ones_do_not()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();

        rig.Feed.Push(Chat("m1", T0.AddMinutes(1)));
        rig.Donations.AddRange(new[]
        {
            new Donation { Id = 7, Username = "Ivan", Amount = 300, Currency = "RUB", CreatedAtUtc = T0.AddMinutes(2) },
            new Donation { Id = -T0.Ticks, Username = "Тестовый", Amount = 1, Currency = "RUB", CreatedAtUtc = T0.AddMinutes(2) },
        });
        rig.Subscribers.AddRange(new[]
        {
            new Subscriber { Key = "s1", Kind = SubscriptionKind.New, Login = "newbie", AtUtc = T0.AddMinutes(3) },
            new Subscriber { Key = "test-1", Kind = SubscriptionKind.New, Login = "test_sub", AtUtc = T0.AddMinutes(3) },
            new Subscriber { Key = "b1", Kind = SubscriptionKind.Bits, Login = "cheer", Bits = 100, AtUtc = T0.AddMinutes(4) },
        });
        rig.Redemptions.AddRange(new[]
        {
            new Redemption { Key = "r1", Login = "mark", RewardTitle = "Трек", Cost = 500, AtUtc = T0.AddMinutes(5) },
            new Redemption { Key = "test-2", Login = "mark", RewardTitle = "Трек", Cost = 500, AtUtc = T0.AddMinutes(5) },
        });
        rig.Moments.AddRange(new[]
        {
            new ChannelMoment { Key = "raid1", Kind = MomentKind.Raid, Login = "big", Viewers = 128, AtUtc = T0.AddMinutes(6) },
            new ChannelMoment { Key = "test-3", Kind = MomentKind.Raid, Login = "big", Viewers = 1, AtUtc = T0.AddMinutes(6) },
        });

        var stream = Assert.Single(rig.Archive.Streams());
        var content = rig.Archive.Read(stream.Id);
        Assert.Equal(new[] { "m1" }, content.Messages.Select(m => m.MessageId));
        Assert.Equal(new[] { "7" }, content.Donations.Select(d => d.Key));
        Assert.Equal(new[] { "s1", "b1" }, content.Subscribers.Select(s => s.Key).OrderByDescending(k => k));
        Assert.Equal(new[] { "r1" }, content.Redemptions.Select(r => r.Key));
        Assert.Equal(new[] { "raid1" }, content.Moments.Select(m => m.Key));
        Assert.Equal((1, 1, 1, 1, 1, 1), (stream.Messages, stream.Donations, stream.Subscriptions, stream.Bits, stream.Rewards, stream.Raids));
    }

    [Fact]
    public async Task Chat_of_another_channel_is_not_recorded()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");

        rig.Feed.Push(Chat("foreign", T0.AddMinutes(1)));

        Assert.Empty(rig.Archive.Read(rig.Archive.Streams()[0].Id).Messages);
    }

    [Fact]
    public async Task After_the_stream_ends_recording_goes_on_for_five_minutes()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();

        rig.Now = T0.AddHours(2);
        rig.GoOffline();
        await rig.CheckAsync();
        rig.Feed.Push(Chat("tail", rig.Now.AddMinutes(1)));
        rig.Now = T0.AddHours(2).AddMinutes(4);
        await rig.CheckAsync();
        Assert.NotNull(rig.Archive.Open);

        rig.Now = T0.AddHours(2).AddMinutes(5);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("late", rig.Now.AddMinutes(1)));

        Assert.Null(rig.Archive.Open);
        var stream = Assert.Single(rig.Archive.Streams());
        Assert.Equal(T0.AddHours(2), stream.EndUtc);
        Assert.Equal(new[] { "tail" }, rig.Archive.Read(stream.Id).Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task A_new_stream_within_fifteen_minutes_continues_the_same_archive_entry()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("first", T0.AddMinutes(1)));
        rig.Now = T0.AddHours(1);
        rig.GoOffline();
        await rig.CheckAsync();
        rig.Now = T0.AddHours(1).AddMinutes(6);
        await rig.CheckAsync();

        rig.Now = T0.AddHours(1).AddMinutes(14);
        rig.GoLive(T0.AddHours(1).AddMinutes(14));
        await rig.CheckAsync();
        rig.Feed.Push(Chat("second", rig.Now.AddMinutes(1)));

        var stream = Assert.Single(rig.Archive.Streams());
        Assert.Equal(T0, stream.StartUtc);
        Assert.Null(stream.EndUtc);
        Assert.Equal(new[] { "first", "second" }, rig.Archive.Read(stream.Id).Messages.Select(m => m.MessageId));
    }

    [Fact]
    public async Task A_stream_after_a_longer_break_is_a_new_archive_entry()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Now = T0.AddHours(1);
        rig.GoOffline();
        await rig.CheckAsync();
        rig.Now = T0.AddHours(1).AddMinutes(6);
        await rig.CheckAsync();

        rig.Now = T0.AddHours(1).AddMinutes(20);
        rig.GoLive(rig.Now);
        await rig.CheckAsync();

        Assert.Equal(2, rig.Archive.Streams().Count);
        Assert.Equal(T0.AddHours(1).AddMinutes(20), rig.Archive.Open!.StartUtc);
    }

    [Fact]
    public async Task Restarting_the_program_mid_stream_keeps_one_entry_without_doubles()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("m1", T0.AddMinutes(1)));
        rig.Donations.AddRange(new[] { new Donation { Id = 9, Username = "Ivan", Amount = 100, Currency = "RUB", CreatedAtUtc = T0.AddMinutes(2) } });

        rig.Restart();
        Assert.NotNull(rig.Archive.Streams()[0].EndUtc);
        rig.Now = T0.AddMinutes(30);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("m2", T0.AddMinutes(31)));

        var stream = Assert.Single(rig.Archive.Streams());
        Assert.Null(stream.EndUtc);
        var content = rig.Archive.Read(stream.Id);
        Assert.Equal(new[] { "m1", "m2" }, content.Messages.Select(m => m.MessageId));
        Assert.Single(content.Donations);
        Assert.Equal(2, stream.Messages);
        Assert.Equal(1, stream.Donations);
    }

    [Fact]
    public async Task A_late_video_and_gift_recipients_reach_the_archive()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        var donation = new Donation { Id = 11, Username = "Ivan", Amount = 500, Currency = "RUB", CreatedAtUtc = T0.AddMinutes(1) };
        var gift = new Subscriber { Key = "g1", Kind = SubscriptionKind.Gift, Login = "santa", DisplayName = "Santa", GiftTotal = 2, AtUtc = T0.AddMinutes(2) };
        rig.Donations.AddRange(new[] { donation });
        rig.Subscribers.AddRange(new[] { gift });

        donation.AttachVideo("abc", "Смешное видео", "https://youtu.be/abc");
        gift.AddRecipient("Anna", "b1");
        gift.AddRecipient("Mark", "b1");

        var content = rig.Archive.Read(rig.Archive.Streams()[0].Id);
        Assert.Equal("https://youtu.be/abc", Assert.Single(content.Donations).VideoUrl);
        Assert.Equal(new[] { "Anna", "Mark" }, Assert.Single(content.Subscribers).Recipients);
        Assert.Equal(1, rig.Archive.Streams()[0].Donations);
        Assert.Equal(1, rig.Archive.Streams()[0].Subscriptions);
    }

    [Fact]
    public async Task A_clip_made_with_the_key_lands_in_the_archive_right_away()
    {
        using var rig = new Rig();
        var clip = new StreamClip { Id = "Own", Url = "https://clips.twitch.tv/Own", Title = "Мой клип", CreatorName = "Streamer", CreatedUtc = T0.AddMinutes(10), Seconds = 30 };
        Assert.False(rig.Recorder.AddClip(clip));
        rig.GoLive(T0);
        await rig.CheckAsync();

        Assert.True(rig.Recorder.AddClip(clip));
        Assert.False(rig.Recorder.AddClip(clip));
        Assert.False(rig.Recorder.AddClip(clip with { Id = "Old", CreatedUtc = T0.AddMinutes(-1) }));

        var stream = rig.Archive.Streams()[0];
        Assert.Equal("Own", Assert.Single(rig.Archive.Read(stream.Id).Clips).Id);
        Assert.Equal(1, stream.Clips);
    }

    [Fact]
    public async Task A_reward_video_title_that_comes_later_reaches_the_archive()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        var reward = new Redemption { Key = "r9", Login = "lisa", DisplayName = "Lisa", RewardTitle = "Видео", Cost = 100, UserInput = "https://youtu.be/abc", AtUtc = T0.AddMinutes(1) };
        rig.Redemptions.AddRange(new[] { reward });

        reward.AttachVideo("https://youtu.be/abc", "", YouTubeImages.Thumbnail("abc"), clip: false);
        reward.AttachVideo("https://youtu.be/abc", "Смешное видео", "https://i.ytimg.com/vi/abc/hq.jpg", clip: false);

        var saved = Assert.Single(rig.Archive.Read(rig.Archive.Streams()[0].Id).Redemptions);
        Assert.Equal(("Смешное видео", "https://i.ytimg.com/vi/abc/hq.jpg"), (saved.VideoTitle, saved.VideoThumbnail));
    }

    [Fact]
    public void Only_changes_worth_keeping_rewrite_an_archived_event()
    {
        var reward = new Redemption { Key = "r", UserInput = "x" };

        Assert.False(ArchiveRecorder.IsUpdate(reward, nameof(Redemption.VideoTitle)));
        reward.AttachVideo("https://youtu.be/abc", "", "", clip: false);
        Assert.True(ArchiveRecorder.IsUpdate(reward, nameof(Redemption.VideoTitle)));
        Assert.True(ArchiveRecorder.IsUpdate(new Donation(), nameof(Donation.HasVideo)));
        Assert.False(ArchiveRecorder.IsUpdate(new Donation(), nameof(Donation.VideoTitle)));
        Assert.True(ArchiveRecorder.IsUpdate(new Subscriber(), nameof(Subscriber.Recipients)));
        Assert.False(ArchiveRecorder.IsUpdate(new Subscriber(), nameof(Subscriber.Message)));
    }

    [Fact]
    public async Task Deleted_messages_and_bans_are_remembered()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("m1", T0.AddMinutes(1)));
        rig.Feed.Push(Chat("m2", T0.AddMinutes(2), userId: "u2", login: "troll"));

        rig.Feed.MarkDeleted("m1");
        rig.Feed.Moderate(new ChatModeration(ChatModerationKind.Ban, "u2", "troll", "Mod"));

        var content = rig.Archive.Read(rig.Archive.Streams()[0].Id);
        Assert.Contains("m1", content.Deleted);
        Assert.Equal(ChatModerationKind.Ban, content.Moderation["m2"].Kind);
    }

    [Fact]
    public async Task Switching_the_archive_off_stops_the_recording_at_once()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();

        rig.Settings.Current.ArchiveEnabled = false;
        rig.Now = T0.AddMinutes(10);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("m1", rig.Now));

        Assert.Null(rig.Archive.Open);
        Assert.Equal(T0.AddMinutes(10), rig.Archive.Streams()[0].EndUtc);
        Assert.Empty(rig.Archive.Read(rig.Archive.Streams()[0].Id).Messages);
        Assert.Equal(1, rig.Live.Calls);
    }

    [Fact]
    public async Task When_Twitch_does_not_answer_the_recording_carries_on()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();

        rig.Live.Failure = new HttpRequestException("нет сети");
        rig.Now = T0.AddMinutes(30);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("m1", rig.Now));

        Assert.NotNull(rig.Archive.Open);
        Assert.Single(rig.Archive.Read(rig.Archive.Streams()[0].Id).Messages);
    }

    [Fact]
    public async Task A_person_history_is_found_in_one_stream_or_in_all_of_them()
    {
        using var rig = new Rig();
        rig.GoLive(T0);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("a1", T0.AddMinutes(1)));
        rig.Feed.Push(Chat("x1", T0.AddMinutes(2), userId: "u9", login: "other"));
        rig.Now = T0.AddHours(1);
        rig.GoOffline();
        await rig.CheckAsync();
        rig.Now = T0.AddHours(2);
        await rig.CheckAsync();
        rig.GoLive(T0.AddDays(1));
        rig.Now = T0.AddDays(1);
        await rig.CheckAsync();
        rig.Feed.Push(Chat("a2", T0.AddDays(1).AddMinutes(1)));
        rig.Feed.MarkDeleted("a2");

        var all = rig.Archive.MessagesOf("u1");
        var first = rig.Archive.MessagesOf("u1", rig.Archive.Streams().Last().Id);

        Assert.Equal(new[] { "a1", "a2" }, all.Select(m => m.Message.MessageId));
        Assert.Equal(new[] { false, true }, all.Select(m => m.Deleted));
        Assert.Equal(2, all.Select(m => m.Stream.Id).Distinct().Count());
        Assert.Equal(new[] { "a1" }, first.Select(m => m.Message.MessageId));
    }

    [Fact]
    public void A_broken_line_in_the_archive_file_is_skipped()
    {
        using var dir = new TempDir();
        using (var archive = ChatArchive.InDataFolder(dir.Path))
        {
            archive.Begin(T0, "", "", ArchiveRecorder.MergeWithin);
            archive.Write(new ArchiveLine { Kind = ArchiveKind.Chat, AtUtc = T0, Chat = Chat("m1", T0) });
        }
        var file = Directory.GetFiles(Path.Combine(dir.Path, ChatArchive.FolderName), "*.jsonl").Single();
        File.AppendAllText(file, "{\"Kind\":\"Chat\",\"Chat\":{\"Mess" + Environment.NewLine);

        using var reopened = ChatArchive.InDataFolder(dir.Path);
        reopened.CloseDangling();
        var stream = Assert.Single(reopened.Streams());
        Assert.Equal(new[] { "m1" }, reopened.Read(stream.Id).Messages.Select(m => m.MessageId));
        Assert.Equal(T0, stream.EndUtc);
    }

    [Fact]
    public void The_archive_is_on_by_default_and_moves_with_the_profile()
    {
        Assert.True(new AppSettings().ArchiveEnabled);
        Assert.Contains(nameof(AppSettings.ArchiveEnabled), Profile.SharedSettings);
    }
}

public class ArchiveTimelineTests
{
    private static readonly DateTime T0 = new(2026, 11, 18, 17, 0, 0, DateTimeKind.Utc);

    private static ArchiveContent Content()
    {
        var content = new ArchiveContent();
        content.Messages.Add(new ChatMessage("m2", "u1", "anna", "Anna", "второе сообщение", Array.Empty<ChatMention>(), T0.AddMinutes(3)));
        content.Messages.Add(new ChatMessage("m1", "u2", "mark", "Mark", "привет всем", Array.Empty<ChatMention>(), T0.AddMinutes(1)));
        content.Deleted.Add("m1");
        content.Donations.Add(new Donation { Id = 1, Username = "Ivan", Amount = 500, Currency = "RUB", Message = "На пиццу", CreatedAtUtc = T0.AddMinutes(2) });
        content.Subscribers.Add(new Subscriber
        {
            Key = "g", Kind = SubscriptionKind.Gift, Login = "santa", DisplayName = "Santa", GiftTotal = 5, Tier = "1000", AtUtc = T0.AddMinutes(4),
            Recipients = new List<string> { "Anna", "Mark" },
        });
        content.Redemptions.Add(new Redemption { Key = "r", Login = "lisa", DisplayName = "Lisa", RewardTitle = "Трек", Cost = 500, UserInput = "Сыграй", AtUtc = T0.AddMinutes(5) });
        content.Moments.Add(new ChannelMoment { Key = "e", Kind = MomentKind.Raid, Login = "big", DisplayName = "Big", Viewers = 128, AtUtc = T0.AddMinutes(6) });
        return content;
    }

    [Fact]
    public void Chat_and_events_are_mixed_by_time()
    {
        var rows = ArchiveTimeline.Build(Content(), T0);

        Assert.Equal(new[] { "m:m1", "d:1", "m:m2", "s:g", "r:r", "e:e" }, rows.Select(r => r.Key));
        Assert.True(rows[0].Deleted);
        Assert.False(rows[2].Deleted);
    }

    [Fact]
    public void Filters_and_search_narrow_the_list()
    {
        var rows = ArchiveTimeline.Build(Content(), T0);
        var all = new HashSet<ArchiveFilter>(ArchiveTimeline.AllFilters);

        Assert.Equal(new[] { "d:1" }, rows.Where(r => ArchiveTimeline.Matches(r, new HashSet<ArchiveFilter> { ArchiveFilter.Donations }, "")).Select(r => r.Key));
        Assert.Equal(new[] { "m:m2", "s:g" }, rows.Where(r => ArchiveTimeline.Matches(r, all, "anna")).Select(r => r.Key));
        Assert.Equal(new[] { "m:m1" }, rows.Where(r => ArchiveTimeline.Matches(r, all, "ПРИВЕТ mark")).Select(r => r.Key));
        Assert.DoesNotContain(rows, r => ArchiveTimeline.Matches(r, new HashSet<ArchiveFilter>(), ""));
    }

    [Fact]
    public void Events_are_described_in_plain_words()
    {
        var rows = ArchiveTimeline.Build(Content(), T0).ToDictionary(r => r.Key);

        Assert.Equal(new ArchiveEventText("Донат", ArchiveEventTone.Donation, "Ivan · 500 ₽", "На пиццу"), rows["d:1"].Event);
        Assert.Equal(new ArchiveEventText("Подарок", ArchiveEventTone.Gift, "Santa · подарил 5 подписок · Tier 1", "Кому: Anna, Mark"), rows["s:g"].Event);
        Assert.Equal(new ArchiveEventText("Награда", ArchiveEventTone.Reward, "Lisa · Трек · 500 баллов", "Сыграй"), rows["r:r"].Event);
        Assert.Equal(new ArchiveEventText("Рейд", ArchiveEventTone.Raid, "Big · привёл 128 зрителей", ""), rows["e:e"].Event);
    }

    [Fact]
    public void A_donation_video_comes_with_its_thumbnail()
    {
        var donation = new Donation { Id = 2, Username = "Ivan", Amount = 100, Currency = "RUB", CreatedAtUtc = T0 };
        donation.AttachVideo("abc", "Смешное видео", "https://youtu.be/abc");

        var text = ArchiveTimeline.Describe(new ArchiveLine { Kind = ArchiveKind.Donation, Donation = donation })!;

        Assert.Equal(("Смешное видео", "https://youtu.be/abc"), (text.VideoTitle, text.VideoUrl));
        Assert.Equal(YouTubeImages.Thumbnail("abc"), text.VideoThumbnail);
        Assert.Equal("Заказанное видео", text.VideoLabel);
        Assert.False(text.VideoIsClip);
    }

    [Fact]
    public void A_donation_video_read_back_from_the_archive_keeps_its_thumbnail()
    {
        var donation = new Donation { Id = 2, Username = "Ivan", Amount = 100, Currency = "RUB", CreatedAtUtc = T0 };
        donation.AttachVideo("abc", "Смешное видео", "https://youtu.be/abc");
        var json = System.Text.Json.JsonSerializer.Serialize(donation);

        var back = System.Text.Json.JsonSerializer.Deserialize<Donation>(json)!;
        var text = ArchiveTimeline.Describe(new ArchiveLine { Kind = ArchiveKind.Donation, Donation = back })!;

        Assert.Equal(YouTubeImages.Thumbnail("abc"), text.VideoThumbnail);
    }

    [Fact]
    public void A_reward_clip_comes_with_its_thumbnail_and_twitch_label()
    {
        var redemption = new Redemption { Key = "r", DisplayName = "Lisa", RewardTitle = "Клип", Cost = 100, UserInput = "https://clips.twitch.tv/x", AtUtc = T0 };
        redemption.AttachVideo("https://clips.twitch.tv/x", "Лучший момент", "https://clips-media/x.jpg", clip: true);

        var text = ArchiveTimeline.Describe(new ArchiveLine { Kind = ArchiveKind.Redemption, Redemption = redemption })!;

        Assert.Equal(("Лучший момент", "https://clips-media/x.jpg", "Клип Twitch", true), (text.VideoTitle, text.VideoThumbnail, text.VideoLabel, text.VideoIsClip));
    }

    [Fact]
    public void An_event_without_a_video_has_no_thumbnail()
    {
        var donation = new Donation { Id = 3, Username = "Ivan", Amount = 100, Currency = "RUB", CreatedAtUtc = T0 };

        var text = ArchiveTimeline.Describe(new ArchiveLine { Kind = ArchiveKind.Donation, Donation = donation })!;

        Assert.Equal(("", "", "", ""), (text.VideoUrl, text.VideoTitle, text.VideoThumbnail, text.VideoLabel));
    }

    private sealed class RowHost : StreamHelper.Ui.IArchiveRowHost
    {
        public readonly List<string> Opened = new();
        public StreamHelper.Ui.ChatEmotes Emotes => null!;
        public StreamHelper.Ui.EmoteAnimator Animator => null!;
        public void OpenHistory(ChatMessage message) { }
        public void OpenLink(string url) => Opened.Add(url);
    }

    private static IEnumerable<T> Descendants<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            if (child is T found) yield return found;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    [Fact]
    public void The_video_card_shows_the_thumbnail_and_opens_the_video_on_click()
    {
        Sta.Run(() =>
        {
            var host = new RowHost();
            var text = new ArchiveEventText("Донат", ArchiveEventTone.Donation, "Ivan · 100 ₽", "", "Смешное видео", "https://youtu.be/abc",
                "https://i.ytimg.com/vi/abc/mqdefault.jpg", "Заказанное видео");

            var card = StreamHelper.Ui.ArchiveRowView.VideoCard(text, host);

            var image = Assert.Single(Descendants<System.Windows.Controls.Image>(card));
            var picture = Assert.IsType<System.Windows.Media.Imaging.BitmapImage>(image.Source);
            Assert.Equal("https://i.ytimg.com/vi/abc/mqdefault.jpg", picture.UriSource.AbsoluteUri);
            var words = Descendants<System.Windows.Controls.TextBlock>(card).Select(t => t.Text).ToList();
            Assert.Contains("Смешное видео", words);
            Assert.Contains("Заказанное видео", words);
            Assert.Equal("https://youtu.be/abc", card.ToolTip);

            card.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent,
            });

            Assert.Equal(new[] { "https://youtu.be/abc" }, host.Opened);
        });
    }

    [Fact]
    public void The_video_card_without_a_thumbnail_still_shows_the_title()
    {
        Sta.Run(() =>
        {
            var text = new ArchiveEventText("Награда", ArchiveEventTone.Reward, "Lisa", "", "https://example.com/v", "https://example.com/v", "", "Видео YouTube");

            var card = StreamHelper.Ui.ArchiveRowView.VideoCard(text, new RowHost());

            Assert.Empty(Descendants<System.Windows.Controls.Image>(card));
            Assert.Contains("https://example.com/v", Descendants<System.Windows.Controls.TextBlock>(card).Select(t => t.Text));
        });
    }

    [Fact]
    public void A_stream_is_summed_up_in_the_list()
    {
        var stream = new ArchiveStream
        {
            StartUtc = T0, EndUtc = T0.AddHours(4).AddMinutes(38), Messages = 1234, Donations = 5, Subscriptions = 3, Bits = 2, Rewards = 0, Raids = 1,
        };

        Assert.Equal("1234 сообщения · 5 донатов · 3 подписки · Bits: 2 · 1 рейд", ArchiveTimeline.Counts(stream));
        Assert.EndsWith("4 ч 38 мин", ArchiveTimeline.StreamTime(stream, false));
        Assert.EndsWith("идёт запись", ArchiveTimeline.StreamTime(stream, true));
        Assert.Equal("45 мин", ArchiveTimeline.Duration(TimeSpan.FromMinutes(45)));
        Assert.Equal("2 ч", ArchiveTimeline.Duration(TimeSpan.FromHours(2)));
    }
}
