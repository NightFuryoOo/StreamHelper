using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class ArchiveClipTests
{
    private static readonly DateTime T0 = new(2026, 11, 19, 18, 19, 7, DateTimeKind.Utc);

    private static StreamClip Clip(string id, DateTime at, string title = "Момент", string creator = "Anna_K", double seconds = 30) => new()
    {
        Id = id, Url = "https://clips.twitch.tv/" + id, Title = title, CreatorId = "11", CreatorName = creator, CreatedUtc = at, Seconds = seconds,
        ThumbnailUrl = "https://thumb/" + id + ".jpg",
    };

    private sealed class FakeClipsApi : IStreamClipsApi
    {
        public readonly List<(string Broadcaster, DateTime From, DateTime To)> Calls = new();
        public List<StreamClip> Clips = new();
        public Exception? Failure;

        public Task<IReadOnlyList<StreamClip>> GetStreamClipsAsync(string broadcasterId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            Calls.Add((broadcasterId, fromUtc, toUtc));
            if (Failure != null) throw Failure;
            return Task.FromResult<IReadOnlyList<StreamClip>>(Clips.ToList());
        }
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClipsApi Api = new();
        public readonly ChatArchive Archive;
        public readonly ArchiveClips Clips;
        public DateTime Now = T0;
        public string Broadcaster = "777";

        public Rig()
        {
            Archive = ChatArchive.InDataFolder(Dir.Path);
            Clips = new ArchiveClips(Archive, Api, () => Broadcaster, () => Now);
        }

        public Task<bool> CheckAsync() => Clips.CheckAsync(CancellationToken.None);

        public void Dispose()
        {
            Archive.Dispose();
            Dir.Dispose();
        }
    }

    [Fact]
    public async Task While_live_the_clips_are_asked_for_every_five_minutes_from_the_stream_start()
    {
        using var rig = new Rig();
        rig.Archive.Begin(T0, "Стрим", "Dead by Daylight", ArchiveRecorder.MergeWithin);
        rig.Api.Clips = new() { Clip("a", T0.AddMinutes(3)) };

        rig.Now = T0.AddMinutes(4);
        Assert.True(await rig.CheckAsync());
        rig.Now = T0.AddMinutes(8);
        Assert.False(await rig.CheckAsync());
        rig.Api.Clips.Add(Clip("b", T0.AddMinutes(7)));
        rig.Now = T0.AddMinutes(9);
        Assert.True(await rig.CheckAsync());

        Assert.Equal(new[] { ("777", T0, T0.AddMinutes(4)), ("777", T0, T0.AddMinutes(9)) }, rig.Api.Calls);
        var stream = rig.Archive.Streams()[0];
        Assert.Equal(new[] { "a", "b" }, rig.Archive.Read(stream.Id).Clips.Select(c => c.Id).OrderBy(id => id));
        Assert.Equal(2, stream.Clips);
        Assert.Equal(T0.AddMinutes(9), stream.ClipsCheckedUtc);
    }

    [Fact]
    public async Task Clips_from_before_the_stream_or_after_its_tail_are_left_out()
    {
        using var rig = new Rig();
        rig.Archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin);
        var end = T0.AddHours(4);
        rig.Archive.End(end);
        rig.Api.Clips = new()
        {
            Clip("before", T0.AddMinutes(-2)),
            Clip("during", T0.AddHours(2)),
            Clip("tail", end.AddMinutes(4)),
            Clip("later", end.AddHours(1)),
        };

        rig.Now = end.AddDays(1);
        Assert.True(await rig.CheckAsync());

        var stream = rig.Archive.Streams()[0];
        Assert.Equal(new[] { "during", "tail" }, rig.Archive.Read(stream.Id).Clips.Select(c => c.Id).OrderBy(id => id));
        Assert.Equal((T0, end + ArchiveRecorder.Tail), (rig.Api.Calls[0].From, rig.Api.Calls[0].To));
    }

    [Fact]
    public async Task A_finished_stream_is_asked_once_more_after_it_settles_and_then_left_alone()
    {
        using var rig = new Rig();
        rig.Archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin);
        var end = T0.AddHours(3);
        rig.Archive.End(end);

        rig.Now = end + ArchiveRecorder.Tail + TimeSpan.FromMinutes(5);
        Assert.False(await rig.CheckAsync());
        rig.Now = end + ArchiveRecorder.Tail + ArchiveClips.Settle;
        Assert.True(await rig.CheckAsync());
        rig.Now = rig.Now.AddHours(5);
        Assert.False(await rig.CheckAsync());

        Assert.Single(rig.Api.Calls);
        Assert.True(ArchiveClips.IsSettled(rig.Archive.Streams()[0]));
    }

    [Fact]
    public async Task Streams_recorded_before_clips_existed_are_filled_in_one_per_check()
    {
        using var rig = new Rig();
        foreach (var day in new[] { 0, 1, 3 })
        {
            rig.Archive.Begin(T0.AddDays(day), "Стрим " + day, "", ArchiveRecorder.MergeWithin);
            rig.Archive.End(T0.AddDays(day).AddHours(4));
        }
        rig.Api.Clips = new() { Clip("old", T0.AddHours(1)), Clip("new", T0.AddDays(3).AddHours(1)) };
        rig.Now = T0.AddDays(10);

        Assert.True(await rig.CheckAsync());
        Assert.True(await rig.CheckAsync());
        Assert.True(await rig.CheckAsync());
        Assert.False(await rig.CheckAsync());

        Assert.Equal(new[] { T0.AddDays(3), T0.AddDays(1), T0 }, rig.Api.Calls.Select(c => c.From));
        var streams = rig.Archive.Streams();
        Assert.Equal("new", Assert.Single(rig.Archive.Read(streams[0].Id).Clips).Id);
        Assert.Empty(rig.Archive.Read(streams[1].Id).Clips);
        Assert.Equal("old", Assert.Single(rig.Archive.Read(streams[2].Id).Clips).Id);
        Assert.Equal(new[] { 1, 0, 1 }, streams.Select(s => s.Clips));
    }

    [Fact]
    public async Task A_failed_ask_is_tried_again_after_five_minutes()
    {
        using var rig = new Rig();
        rig.Archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin);
        rig.Api.Failure = new HttpRequestException("Twitch: HTTP 500.");

        rig.Now = T0.AddMinutes(1);
        Assert.False(await rig.CheckAsync());
        rig.Now = T0.AddMinutes(3);
        Assert.False(await rig.CheckAsync());
        rig.Api.Failure = null;
        rig.Api.Clips = new() { Clip("a", T0.AddMinutes(2)) };
        rig.Now = T0.AddMinutes(6);
        Assert.True(await rig.CheckAsync());

        Assert.Equal(2, rig.Api.Calls.Count);
        Assert.Single(rig.Archive.Read(rig.Archive.Streams()[0].Id).Clips);
    }

    [Fact]
    public async Task Without_a_twitch_account_nothing_is_asked()
    {
        using var rig = new Rig { Broadcaster = "" };
        rig.Archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin);

        Assert.False(await rig.CheckAsync());
        Assert.Empty(rig.Api.Calls);
    }

    [Fact]
    public void A_renamed_clip_is_updated_without_counting_it_twice()
    {
        using var dir = new TempDir();
        using var archive = ChatArchive.InDataFolder(dir.Path);
        var stream = archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin);
        var events = new List<(string Id, bool Update)>();
        archive.Written += (_, line, update) => events.Add((line.Clip!.Id, update));

        Assert.Equal(1, archive.SaveClips(stream.Id, new[] { Clip("a", T0.AddMinutes(1), title: "Стрим") }, T0.AddMinutes(2)));
        Assert.Equal(0, archive.SaveClips(stream.Id, new[] { Clip("a", T0.AddMinutes(1), title: "Стрим") }, T0.AddMinutes(7)));
        Assert.Equal(0, archive.SaveClips(stream.Id, new[] { Clip("a", T0.AddMinutes(1), title: "Лучший момент", seconds: 21) }, T0.AddMinutes(12)));

        var saved = Assert.Single(archive.Read(stream.Id).Clips);
        Assert.Equal(("Лучший момент", 21.0), (saved.Title, saved.Seconds));
        Assert.Equal(1, archive.Streams()[0].Clips);
        Assert.Equal(new[] { ("a", false), ("a", true) }, events);
    }

    [Fact]
    public void Clips_of_a_finished_stream_are_kept_after_a_restart()
    {
        using var dir = new TempDir();
        string id;
        using (var archive = ChatArchive.InDataFolder(dir.Path))
        {
            id = archive.Begin(T0, "Стрим", "", ArchiveRecorder.MergeWithin).Id;
            archive.End(T0.AddHours(2));
            Assert.Equal(2, archive.SaveClips(id, new[] { Clip("a", T0.AddMinutes(5)), Clip("b", T0.AddMinutes(50)) }, T0.AddDays(1)));
        }

        using var again = ChatArchive.InDataFolder(dir.Path);
        var stream = Assert.Single(again.Streams());
        Assert.Equal((2, T0.AddDays(1)), (stream.Clips, stream.ClipsCheckedUtc));
        Assert.Equal(new[] { "a", "b" }, again.Read(id).Clips.Select(c => c.Id).OrderBy(c => c));
        Assert.Equal(0, again.SaveClips(id, new[] { Clip("a", T0.AddMinutes(5)) }, null));
    }

    [Fact]
    public async Task Clips_of_a_stream_are_asked_for_its_time_window_page_by_page()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (request, _) => request.QueryString["after"] == null
            ? (200, """{"data":[{"id":"One","url":"https://clips.twitch.tv/One","title":"Первый","creator_id":"11","creator_name":"Anna_K","created_at":"2026-11-19T19:00:00Z","duration":27.6,"thumbnail_url":"https://t/1.jpg"}],"pagination":{"cursor":"c1"}}""")
            : (200, """{"data":[{"id":"Two","url":"https://clips.twitch.tv/Two","title":" Второй ","creator_id":"12","creator_name":"Mark","created_at":"2026-11-19T20:00:00Z","duration":30,"thumbnail_url":"https://t/2.jpg"}],"pagination":{}}""");
        var client = new TwitchClient(MakeFollower.Settings(dir), new HttpClient(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var clips = await client.GetStreamClipsAsync("777", T0, T0.AddHours(4), CancellationToken.None);

        Assert.Equal(new[] { "One", "Two" }, clips.Select(c => c.Id));
        Assert.Equal("Второй", clips[1].Title);
        Assert.Equal(new DateTime(2026, 11, 19, 19, 0, 0, DateTimeKind.Utc), clips[0].CreatedUtc);
        Assert.Equal(2, twitch.Requests.Count);
        var first = Uri.UnescapeDataString(twitch.Requests[0]);
        Assert.StartsWith("GET /helix/clips?broadcaster_id=777&started_at=2026-11-19T18:19:07Z&ended_at=2026-11-19T22:19:07Z&first=100 ", first);
        Assert.Contains("&after=c1", Uri.UnescapeDataString(twitch.Requests[1]));
    }

    [Fact]
    public void A_clip_list_from_twitch_is_read_and_broken_entries_are_skipped()
    {
        var (clips, cursor) = TwitchClient.ParseStreamClips("""
            {"data":[
              {"id":"Good","url":"https://clips.twitch.tv/Good","title":"Смешно","creator_id":"11","creator_name":"Anna_K","created_at":"2026-11-19T19:00:00Z","duration":12.4,"thumbnail_url":"https://t/g.jpg"},
              {"id":"NoTime","title":"Без времени"},
              {"title":"Без номера","created_at":"2026-11-19T19:00:00Z"},
              {"id":"NoUrl","created_at":"2026-11-19T19:05:00Z"}
            ],"pagination":{"cursor":"next"}}
            """);

        Assert.Equal("next", cursor);
        Assert.Equal(new[] { "Good", "NoUrl" }, clips.Select(c => c.Id));
        Assert.Equal(new StreamClip
        {
            Id = "Good", Url = "https://clips.twitch.tv/Good", Title = "Смешно", CreatorId = "11", CreatorName = "Anna_K",
            CreatedUtc = new DateTime(2026, 11, 19, 19, 0, 0, DateTimeKind.Utc), Seconds = 12.4, ThumbnailUrl = "https://t/g.jpg",
        }, clips[0]);
        Assert.Equal("https://clips.twitch.tv/NoUrl", clips[1].Url);
        Assert.Empty(TwitchClient.ParseStreamClips("not json").Clips);
    }

    [Fact]
    public void A_clip_row_shows_who_made_it_and_opens_the_clip()
    {
        var line = new ArchiveLine { Kind = ArchiveKind.Clip, AtUtc = T0.AddMinutes(30), Clip = Clip("a", T0.AddMinutes(30), title: "Лучший момент", seconds: 27.6) };

        var row = new ArchiveRow(line, T0);

        Assert.Equal(("c:a", ArchiveFilter.Clips), (row.Key, row.Filter));
        Assert.Equal(new ArchiveEventText("Клип", ArchiveEventTone.Clip, "Anna_K · 28 с", "", "Лучший момент", "https://clips.twitch.tv/a", "https://thumb/a.jpg", "Клип Twitch", true), row.Event);
        Assert.True(ArchiveTimeline.Matches(row, new HashSet<ArchiveFilter> { ArchiveFilter.Clips }, "anna момент"));
        Assert.False(ArchiveTimeline.Matches(row, new HashSet<ArchiveFilter> { ArchiveFilter.Messages, ArchiveFilter.Raids }, ""));
        Assert.Equal("https://clips.twitch.tv/b", ArchiveTimeline.Describe(new ArchiveLine { Kind = ArchiveKind.Clip, Clip = Clip("b", T0, title: "") })!.VideoTitle);
    }

    [Fact]
    public void Clips_take_their_place_in_time_and_are_counted_in_the_list()
    {
        var content = new ArchiveContent();
        content.Messages.Add(new ChatMessage("m1", "11", "anna_k", "Anna_K", "раз", Array.Empty<ChatMention>(), T0.AddMinutes(1)));
        content.Messages.Add(new ChatMessage("m2", "11", "anna_k", "Anna_K", "два", Array.Empty<ChatMention>(), T0.AddMinutes(3)));
        content.Clips.Add(Clip("a", T0.AddMinutes(2)));

        var rows = ArchiveTimeline.Build(content, T0);

        Assert.Equal(new[] { "m:m1", "c:a", "m:m2" }, rows.Select(r => r.Key));
        Assert.Equal("10 сообщений · 3 клипа", ArchiveTimeline.Counts(new ArchiveStream { Messages = 10, Clips = 3 }));
        Assert.Equal("1 сообщение · 1 клип", ArchiveTimeline.Counts(new ArchiveStream { Messages = 1, Clips = 1 }));
    }

    [Fact]
    public void A_late_row_goes_in_by_its_time_not_at_the_end()
    {
        var rows = new[] { 1, 2, 2, 4 }
            .Select(minute => new ArchiveRow(new ArchiveLine { Kind = ArchiveKind.Clip, AtUtc = T0.AddMinutes(minute), Clip = Clip("x" + minute, T0.AddMinutes(minute)) }, T0))
            .ToList();

        Assert.Equal(0, ArchiveTimeline.PositionOf(rows, T0));
        Assert.Equal(3, ArchiveTimeline.PositionOf(rows, T0.AddMinutes(2)));
        Assert.Equal(3, ArchiveTimeline.PositionOf(rows, T0.AddMinutes(3)));
        Assert.Equal(4, ArchiveTimeline.PositionOf(rows, T0.AddMinutes(9)));
        Assert.Equal(0, ArchiveTimeline.PositionOf(Array.Empty<ArchiveRow>(), T0));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(0.4, "1 с")]
    [InlineData(27.6, "28 с")]
    [InlineData(60, "60 с")]
    public void The_clip_length_is_shown_in_whole_seconds(double seconds, string expected)
    {
        Assert.Equal(expected, ArchiveTimeline.ClipLength(seconds));
    }
}
