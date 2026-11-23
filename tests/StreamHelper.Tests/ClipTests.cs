using System.Net;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class ClipTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat user:write:chat clips:edit")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Fact]
    public async Task A_clip_is_made_of_your_own_stream_even_when_the_chat_shows_another_channel()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (_, _) => (202, """{"data":[{"id":"FiveWordsForClipSlug","edit_url":"https://www.twitch.tv/me/clip/FiveWordsForClipSlug"}]}""");
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var start = await client.CreateClipAsync(CancellationToken.None);

        Assert.Equal(new ClipStart(true, "", "FiveWordsForClipSlug", "https://www.twitch.tv/me/clip/FiveWordsForClipSlug"), start);
        Assert.StartsWith("POST /helix/clips?broadcaster_id=777 ", Assert.Single(twitch.Requests));
    }

    [Fact]
    public async Task The_clip_link_goes_to_your_own_chat_even_when_the_chat_shows_another_channel()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        twitch.Handler = (_, _) => (200, """{"data":[{"message_id":"m1","is_sent":true}]}""");
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        Assert.True((await client.SendOwnChatMessageAsync("Клип: https://clips.twitch.tv/x", CancellationToken.None)).Success);
        Assert.True((await client.SendChatMessageAsync("обычное", null, CancellationToken.None)).Success);

        Assert.Contains("\"broadcaster_id\":\"777\"", twitch.Requests[0]);
        Assert.Contains("\"broadcaster_id\":\"555\"", twitch.Requests[1]);
    }

    [Fact]
    public async Task Without_the_clip_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var twitch = new MockDa();
        var client = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(twitch.BaseUrl));

        var start = await client.CreateClipAsync(CancellationToken.None);

        Assert.False(start.Success);
        Assert.Contains("переподключи Twitch", start.Message);
        Assert.Empty(twitch.Requests);
    }

    [Theory]
    [InlineData(404, "The broadcaster in the broadcaster_id query parameter must be broadcasting live.", "Клип можно сделать только во время эфира.")]
    [InlineData(403, "The broadcaster has restricted the ability to capture clips to followers and/or subscribers only.", "Клипы на канале доступны только фолловерам или подписчикам.")]
    [InlineData(403, "The specified broadcaster has not enabled clips on their channel.", "Клипы на канале выключены. Включи их в настройках канала на Twitch.")]
    [InlineData(400, "The category is not clippable.", "В этой категории Twitch не разрешает клипы.")]
    [InlineData(429, "", "Слишком часто, подожди немного.")]
    [InlineData(500, "", "Twitch: HTTP 500.")]
    public void A_refusal_is_explained(int status, string message, string expected)
    {
        Assert.Equal(expected, TwitchClient.DescribeClipFailure((HttpStatusCode)status, message));
    }

    private sealed class FakeClips : IClipMakerApi
    {
        public ClipStart Start = new(true, "", "Slug1", "https://edit");
        public int ReadyAfter = 2;
        public int Checks;
        public ChatSendResult ChatAnswer = new(true, "");
        public readonly List<string> Chat = new();
        public TaskCompletionSource? Hold;

        public async Task<ClipStart> CreateClipAsync(CancellationToken ct)
        {
            if (Hold != null) await Hold.Task;
            return Start;
        }

        public Task<LinkPreview?> GetClipAsync(string slug, CancellationToken ct)
        {
            Checks++;
            return Task.FromResult(Checks >= ReadyAfter
                ? new LinkPreview(PreviewKind.TwitchClip, "https://clips.twitch.tv/" + slug, "Вот это момент", "me", "me", 0, TimeSpan.FromSeconds(30), "https://thumb/" + slug + ".jpg")
                : null);
        }

        public Task<ChatSendResult> SendOwnChatMessageAsync(string text, CancellationToken ct)
        {
            Chat.Add(text);
            return Task.FromResult(ChatAnswer);
        }
    }

    [Fact]
    public async Task A_made_clip_is_waited_for_then_its_link_goes_to_chat()
    {
        var api = new FakeClips();
        var waits = new List<TimeSpan>();
        var asked = new DateTime(2026, 11, 23, 19, 40, 0, DateTimeKind.Utc);
        var maker = new ClipMaker(api, wait => { waits.Add(wait); return Task.CompletedTask; }, now: () => asked);
        ClipOutcome? started = null;

        var outcome = await maker.MakeAsync(s => started = s);

        Assert.Equal(ClipMaker.Started, started?.Title);
        Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) }, waits);
        Assert.Equal(new[] { "Клип: https://clips.twitch.tv/Slug1" }, api.Chat);
        Assert.Equal(new ClipOutcome(true, "Клип готов", "Ссылка отправлена в чат", "https://thumb/Slug1.jpg", "Вот это момент"), outcome with { Clip = null });
        Assert.Equal(new StreamClip
        {
            Id = "Slug1", Url = "https://clips.twitch.tv/Slug1", Title = "Вот это момент", CreatorName = "me", CreatedUtc = asked, Seconds = 30,
            ThumbnailUrl = "https://thumb/Slug1.jpg",
        }, outcome.Clip);
        Assert.False(maker.IsBusy);
    }

    [Fact]
    public async Task A_refused_clip_sends_nothing_to_chat()
    {
        var api = new FakeClips { Start = new ClipStart(false, "Клип можно сделать только во время эфира.") };
        var maker = new ClipMaker(api, _ => Task.CompletedTask);

        var outcome = await maker.MakeAsync();

        Assert.Equal(new ClipOutcome(false, "Клип не сделан", "Клип можно сделать только во время эфира."), outcome);
        Assert.Null(outcome.Clip);
        Assert.Empty(api.Chat);
        Assert.Equal(0, api.Checks);
    }

    [Fact]
    public async Task A_clip_that_never_shows_up_is_given_up_after_a_minute_without_a_chat_link()
    {
        var api = new FakeClips { ReadyAfter = int.MaxValue };
        var total = TimeSpan.Zero;
        var maker = new ClipMaker(api, wait => { total += wait; return Task.CompletedTask; });

        var outcome = await maker.MakeAsync();

        Assert.False(outcome.Success);
        Assert.Equal("Клип не появился", outcome.Title);
        Assert.Equal(ClipMaker.GiveUp, total);
        Assert.Equal(12, api.Checks);
        Assert.Empty(api.Chat);
    }

    [Fact]
    public async Task A_second_press_while_the_clip_is_made_does_not_make_another()
    {
        var api = new FakeClips { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var maker = new ClipMaker(api, _ => Task.CompletedTask);

        var first = maker.MakeAsync();
        var second = await maker.MakeAsync();
        api.Hold.SetResult();
        var done = await first;

        Assert.Equal(ClipMaker.Busy, second.Line);
        Assert.True(done.Success);
        Assert.Single(api.Chat);
    }

    [Fact]
    public async Task When_chat_refuses_the_link_the_clip_is_still_reported_as_made()
    {
        var api = new FakeClips { ReadyAfter = 1, ChatAnswer = new ChatSendResult(false, "Медленный режим") };
        var maker = new ClipMaker(api, _ => Task.CompletedTask);

        var outcome = await maker.MakeAsync();

        Assert.True(outcome.Success);
        Assert.Equal("В чат не отправлено: Медленный режим", outcome.Line);
    }

    [Fact]
    public async Task With_the_chat_link_switched_off_the_clip_is_made_but_nothing_goes_to_chat()
    {
        var api = new FakeClips { ReadyAfter = 1 };
        var maker = new ClipMaker(api, _ => Task.CompletedTask, linkToChat: () => false);

        var outcome = await maker.MakeAsync();

        Assert.Equal(new ClipOutcome(true, "Клип готов", ClipMaker.SavedOnChannel, "https://thumb/Slug1.jpg", "Вот это момент"), outcome with { Clip = null });
        Assert.Equal("Slug1", outcome.Clip?.Id);
        Assert.Empty(api.Chat);
    }

    [Fact]
    public void The_chat_link_is_off_by_default_and_moves_with_the_profile()
    {
        Assert.False(new AppSettings().ClipLinkToChat);
        Assert.Contains(nameof(AppSettings.ClipLinkToChat), Profile.SharedSettings);
    }

    [Fact]
    public void The_clip_key_has_a_name_and_the_right_is_asked_at_login()
    {
        Assert.Contains(HotkeyAction.Clip, HotkeyActions.All);
        Assert.Equal("Сделать клип", HotkeyActions.Title(HotkeyAction.Clip));
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasClipsScope);
    }
}
