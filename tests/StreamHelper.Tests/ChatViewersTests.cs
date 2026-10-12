using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatViewersTests
{
    private sealed class Streams : ILiveStreamApi
    {
        private static readonly DateTime Started = new(2026, 10, 12, 11, 0, 0, DateTimeKind.Utc);

        public Dictionary<string, int> Live { get; } = new();
        public List<string> Asked { get; } = new();
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Hold { get; set; }

        public async Task<IReadOnlyList<LiveStream>> GetLiveStreamsAsync(IReadOnlyList<string> userIds, CancellationToken ct)
        {
            Asked.Add(string.Join(",", userIds));
            if (Hold != null) await Hold.Task;
            if (Failure != null) throw Failure;
            return userIds.Where(Live.ContainsKey).Select(id => new LiveStream(id, "login" + id, "Game", "Title", Live[id], Started)).ToList();
        }
    }

    private static AppSettings Connected() => new() { TwitchUserId = "777", TwitchRefreshTokenProtected = "x" };

    [Fact]
    public async Task A_live_channel_shows_its_viewers_and_an_offline_one_shows_nothing()
    {
        var api = new Streams();
        api.Live["777"] = 1234;
        var settings = Connected();
        var viewers = new ChatViewers(api, () => settings);
        var changes = 0;
        viewers.Changed += () => changes++;

        await viewers.RefreshAsync();
        Assert.Equal(1234, viewers.Stream!.Viewers);
        await viewers.RefreshAsync();
        Assert.Equal(1, changes);

        api.Live.Remove("777");
        await viewers.RefreshAsync();
        Assert.Null(viewers.Stream);
        Assert.Equal(2, changes);
        Assert.Equal(new[] { "777", "777", "777" }, api.Asked.ToArray());
    }

    [Fact]
    public async Task The_chosen_channel_is_asked_for_not_the_own_one()
    {
        var api = new Streams();
        api.Live["555"] = 40;
        var settings = Connected();
        settings.TwitchChannelId = "555";
        var viewers = new ChatViewers(api, () => settings);

        await viewers.RefreshAsync();

        Assert.Equal("555", viewers.Stream!.UserId);
        Assert.Equal(new[] { "555" }, api.Asked.ToArray());
    }

    [Fact]
    public async Task Switched_off_or_without_Twitch_nothing_is_asked_and_the_count_goes_away()
    {
        var api = new Streams();
        api.Live["777"] = 5;
        var settings = Connected();
        var viewers = new ChatViewers(api, () => settings);
        await viewers.RefreshAsync();
        Assert.NotNull(viewers.Stream);

        settings.ChatShowViewers = false;
        await viewers.RefreshAsync();
        Assert.Null(viewers.Stream);

        settings.ChatShowViewers = true;
        settings.TwitchRefreshTokenProtected = "";
        await viewers.RefreshAsync();
        Assert.Null(viewers.Stream);
        Assert.Single(api.Asked);
        Assert.True(new AppSettings().ChatShowViewers);
    }

    [Fact]
    public async Task A_failed_request_keeps_the_last_count_for_five_minutes_then_drops_it()
    {
        var api = new Streams();
        api.Live["777"] = 99;
        var settings = Connected();
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var viewers = new ChatViewers(api, () => settings, () => now);
        await viewers.RefreshAsync();

        api.Failure = new HttpRequestException("down");
        now = now.AddMinutes(4);
        await viewers.RefreshAsync();
        Assert.Equal(99, viewers.Stream!.Viewers);

        now = now.AddMinutes(2);
        await viewers.RefreshAsync();
        Assert.Null(viewers.Stream);
    }

    [Fact]
    public async Task An_answer_for_the_previous_channel_is_thrown_away_and_the_new_channel_is_asked()
    {
        var api = new Streams { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        api.Live["777"] = 10;
        api.Live["555"] = 20;
        var settings = Connected();
        var viewers = new ChatViewers(api, () => settings);

        var first = viewers.RefreshAsync();
        settings.TwitchChannelId = "555";
        viewers.Clear();
        await viewers.RefreshAsync();
        api.Hold.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("555", viewers.Stream!.UserId);
        Assert.Equal(20, viewers.Stream.Viewers);
        Assert.Equal(new[] { "777", "555" }, api.Asked.ToArray());
    }
}
