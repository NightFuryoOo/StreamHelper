using System.Net;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class PinTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat moderator:manage:chat_messages")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private const string PinnedJson = """
        {"data":[{"message_id":"m-1","broadcaster_id":"555","sender_user_id":"42","sender_user_login":"anna_k","sender_user_name":"Anna_K",
        "pinned_by_user_id":"9","pinned_by_user_login":"sever31ru","pinned_by_user_name":"SeveR31ru",
        "message":{"text":"привет Kappa","fragments":[{"type":"text","text":"привет "},{"type":"emote","text":"Kappa","emote":{"id":"25","format":["static"]}}]},
        "starts_at":"2026-11-09T10:00:00Z","ends_at":"2026-11-09T10:05:00Z","updated_at":"2026-11-09T10:00:00Z"}]}
        """;

    [Fact]
    public async Task The_pinned_message_is_read_with_its_author_who_pinned_it_and_until_when()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, PinnedJson);
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var check = await client.GetPinnedAsync(CancellationToken.None);

        Assert.True(check.Success, check.Error);
        Assert.StartsWith("GET /helix/chat/pins?broadcaster_id=555&moderator_id=777 ", Assert.Single(da.Requests));
        var pinned = check.Pinned!;
        Assert.Equal("m-1", pinned.Message.MessageId);
        Assert.Equal("anna_k", pinned.Message.ChatterLogin);
        Assert.Equal("Anna_K", pinned.Message.ChatterName);
        Assert.Equal("привет Kappa", pinned.Message.Text);
        Assert.Equal(new[] { "text", "emote" }, pinned.Message.Fragments!.Select(f => f.Type));
        Assert.Equal("25", pinned.Message.Fragments![1].EmoteId);
        Assert.Equal("SeveR31ru", pinned.PinnedByLabel);
        Assert.Equal(new DateTime(2026, 11, 9, 10, 5, 0, DateTimeKind.Utc), pinned.EndsUtc);
    }

    [Fact]
    public void A_pin_until_the_end_of_the_stream_has_no_end_and_an_empty_list_means_nothing_is_pinned()
    {
        var forever = TwitchClient.ParsePinned(PinnedJson.Replace("\"ends_at\":\"2026-11-09T10:05:00Z\"", "\"ends_at\":null"));

        Assert.NotNull(forever);
        Assert.Null(forever!.EndsUtc);
        Assert.Null(TwitchClient.ParsePinned("{\"data\":[]}"));
        Assert.Null(TwitchClient.ParsePinned("not json"));
    }

    [Fact]
    public async Task Pinning_sends_the_message_and_the_chosen_time()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (204, "");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.True((await client.PinAsync("abc-1", 300, CancellationToken.None)).Success);
        Assert.True((await client.PinAsync("abc-2", null, CancellationToken.None)).Success);
        Assert.True((await client.UnpinAsync("abc-2", CancellationToken.None)).Success);

        Assert.StartsWith("PUT /helix/chat/pins?broadcaster_id=777&moderator_id=777&message_id=abc-1&duration_seconds=300 ", da.Requests[0]);
        Assert.StartsWith("PUT /helix/chat/pins?broadcaster_id=777&moderator_id=777&message_id=abc-2 ", da.Requests[1]);
        Assert.StartsWith("DELETE /helix/chat/pins?broadcaster_id=777&moderator_id=777&message_id=abc-2 ", da.Requests[2]);
    }

    [Fact]
    public async Task Unpinning_a_message_that_is_no_longer_pinned_counts_as_done()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (404, "{\"message\":\"not found\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.True((await client.UnpinAsync("gone", CancellationToken.None)).Success);
        Assert.Equal("Twitch не нашёл это сообщение: возможно, его удалили.", (await client.PinAsync("gone", 60, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task Without_the_right_or_a_message_id_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var noRight = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var ready = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Contains("переподключить Twitch", (await noRight.PinAsync("abc", 60, CancellationToken.None)).Message);
        Assert.False((await noRight.GetPinnedAsync(CancellationToken.None)).Success);
        Assert.False((await ready.PinAsync("  ", 60, CancellationToken.None)).Success);
        Assert.Empty(da.Requests);
    }

    [Theory]
    [InlineData(409, "", null, "Это сообщение уже закреплено.")]
    [InlineData(400, "The duration_seconds value is invalid.", null, "Twitch не принял срок закрепления.")]
    [InlineData(403, "", null, "Twitch не разрешил закреплять сообщения на этом канале.")]
    [InlineData(403, "", "Big_Streamer", "Twitch не разрешил: ты не модератор канала Big_Streamer.")]
    [InlineData(429, "", null, "Слишком часто, подожди немного.")]
    [InlineData(500, "", null, "Twitch: HTTP 500.")]
    public void A_refusal_is_explained(int status, string message, string? other, string expected)
    {
        Assert.Equal(expected, TwitchClient.DescribePinFailure((HttpStatusCode)status, message, other));
    }

    private static PinnedMessage Pin(string id, DateTime? ends, string by = "SeveR31ru") =>
        new(new ChatMessage(id, "42", "anna_k", "Anna_K", "текст", Array.Empty<ChatMention>(), DateTime.UtcNow), "", by, DateTime.UtcNow, ends);

    [Fact]
    public void The_card_says_who_pinned_and_how_long_is_left()
    {
        var now = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
        var timed = Pin("m", now.AddSeconds(272));
        var stream = Pin("m", null, by: "");

        Assert.Equal("Закреплено · SeveR31ru", PinCardText.Title(timed));
        Assert.Equal("Закреплено", PinCardText.Title(stream));
        Assert.Equal("ещё 4:32", PinCardText.Status(timed, now));
        Assert.Equal("4:32", PinCardText.ShortStatus(timed, now));
        Assert.Equal("до конца стрима", PinCardText.Status(stream, now));
        Assert.Equal("до конца", PinCardText.ShortStatus(stream, now));
        Assert.False(PinCardText.Expired(timed, now));
        Assert.True(PinCardText.Expired(timed, now.AddSeconds(272)));
        Assert.False(PinCardText.Expired(stream, now.AddDays(1)));
    }

    [Fact]
    public void The_menu_offers_the_agreed_times()
    {
        Assert.Equal(new[] { "1 мин", "5 мин", "10 мин", "30 мин", "До конца стрима" }, PinCardText.Choices.Select(c => c.Text));
        Assert.Equal(new int?[] { 60, 300, 600, 1800, null }, PinCardText.Choices.Select(c => c.Seconds));
    }

    [Fact]
    public void Folding_the_pin_card_shows_the_short_time()
    {
        var now = DateTime.UtcNow;
        var card = new PinCardModel(Pin("m", now.AddSeconds(90)), now, canUnpin: true, collapsed: false);
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        card.Collapsed = true;

        Assert.Equal(card.ShortStatus, card.ShownStatus);
        Assert.Contains(nameof(PinCardModel.ShownStatus), changed);
        Assert.Contains(nameof(PinCardModel.BodyShown), changed);
        Assert.Equal("▸", card.FoldGlyph);
    }

    private sealed class FakePins : IModerationApi
    {
        public int Reads;
        public Func<Task<PinCheck>> Next = () => Task.FromResult(new PinCheck(true, null));

        public Task<PinCheck> GetPinnedAsync(CancellationToken ct)
        {
            Reads++;
            return Next();
        }

        public Task<ModerationResult> PinAsync(string messageId, int? durationSeconds, CancellationToken ct) => Task.FromResult(new ModerationResult(true, ""));
        public Task<ModerationResult> UnpinAsync(string messageId, CancellationToken ct) => Task.FromResult(new ModerationResult(true, ""));
        public Task<ModerationResult> BanAsync(string userId, int? durationSeconds, CancellationToken ct) => throw new NotSupportedException();
        public Task<ModerationResult> UnbanAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ModerationResult> ShoutoutAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ModerationResult> DeleteChatMessageAsync(string messageId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static AppSettings Owner()
    {
        var settings = new AppSettings { TwitchUserId = "777", TwitchScopes = "moderator:manage:chat_messages" };
        settings.TwitchRefreshToken = "r";
        settings.TwitchAccessToken = "a";
        return settings;
    }

    [Fact]
    public async Task The_watcher_reports_a_new_pin_once_and_keeps_it_when_twitch_does_not_answer()
    {
        var api = new FakePins();
        var watcher = new PinWatcher(api, Owner);
        var changes = 0;
        watcher.Changed += () => changes++;
        var pin = Pin("m-1", DateTime.UtcNow.AddMinutes(5));

        api.Next = () => Task.FromResult(new PinCheck(true, pin));
        await watcher.RefreshAsync();
        await watcher.RefreshAsync();
        api.Next = () => Task.FromResult(new PinCheck(false, null, "Нет связи с Twitch"));
        await watcher.RefreshAsync();

        Assert.Same(pin, watcher.Pinned);
        Assert.Equal(1, changes);
        Assert.Equal(3, api.Reads);

        api.Next = () => Task.FromResult(new PinCheck(true, null));
        await watcher.RefreshAsync();

        Assert.Null(watcher.Pinned);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task An_answer_that_was_on_its_way_before_your_own_pin_does_not_undo_it()
    {
        var api = new FakePins();
        var watcher = new PinWatcher(api, Owner);
        var slow = new TaskCompletionSource<PinCheck>();
        api.Next = () => slow.Task;

        var reading = watcher.RefreshAsync();
        var mine = Pin("mine", null);
        watcher.Show(mine);
        slow.SetResult(new PinCheck(true, Pin("old", null)));
        await reading;

        Assert.Same(mine, watcher.Pinned);
    }

    [Fact]
    public async Task In_viewer_mode_the_watcher_does_not_ask_twitch()
    {
        var api = new FakePins();
        var settings = Owner();
        settings.SelectChannel("555", "big", "Big");
        settings.TwitchChannelAsViewer = true;
        var watcher = new PinWatcher(api, () => settings);

        await watcher.RefreshAsync();

        Assert.Equal(0, api.Reads);
        Assert.Null(watcher.Pinned);
        Assert.False(watcher.Allowed);
    }
}
