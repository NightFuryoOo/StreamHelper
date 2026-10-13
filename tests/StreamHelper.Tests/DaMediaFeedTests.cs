using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using StreamHelper.Models;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class DaMediaFeedTests
{
    private const string Captured =
        "42[\"donation\",\"{\\\"id\\\":192395751,\\\"alert_type\\\":\\\"1\\\",\\\"is_shown\\\":\\\"0\\\",\\\"additional_data\\\":\\\"{\\\\\\\"randomness\\\\\\\":474,\\\\\\\"is_commission_covered\\\\\\\":1," +
        "\\\\\\\"media_data\\\\\\\":{\\\\\\\"id\\\\\\\":\\\\\\\"UQWK8jtYlnU\\\\\\\",\\\\\\\"title\\\\\\\":\\\\\\\"WHEN DBD IS NOT SO COMPETITIVE | COMPILATION\\\\\\\"," +
        "\\\\\\\"url\\\\\\\":\\\\\\\"https:\\\\\\\\/\\\\\\\\/www.youtube.com\\\\\\\\/watch?v=UQWK8jtYlnU\\\\\\\"}}\\\",\\\"username\\\":\\\"viewer\\\",\\\"amount\\\":\\\"200.00\\\"," +
        "\\\"currency\\\":\\\"RUB\\\",\\\"message\\\":null,\\\"date_created\\\":\\\"2026-10-12 21:08:21\\\",\\\"_is_test_alert\\\":false,\\\"message_type\\\":\\\"text\\\"}\"]";

    [Fact]
    public void The_widget_link_from_DonationAlerts_gives_its_key_and_other_text_does_not()
    {
        const string link = "https://www.donationalerts.com/widget/alerts?group_id=1&token=AbCdEfGh1234567890Xy";

        Assert.Equal("AbCdEfGh1234567890Xy", DaMediaFeed.TokenOf(link));
        Assert.True(DaMediaFeed.IsWidgetLink(link));
        Assert.True(DaMediaFeed.IsWidgetLink("  https://www.donationalerts.com/widget/media?token=abc  "));
        Assert.False(DaMediaFeed.IsWidgetLink("http://www.donationalerts.com/widget/alerts?token=abc"));
        Assert.False(DaMediaFeed.IsWidgetLink("https://evil.example/widget/alerts?token=abc"));
        Assert.False(DaMediaFeed.IsWidgetLink("https://www.donationalerts.com/widget/alerts?group_id=1"));
        Assert.False(DaMediaFeed.IsWidgetLink("https://www.donationalerts.com/dashboard?token=abc"));
        Assert.False(DaMediaFeed.IsWidgetLink("AbCdEfGh1234567890Xy"));
        Assert.Null(DaMediaFeed.TokenOf("not a link"));
    }

    [Fact]
    public void The_channel_address_is_read_from_the_widget_page()
    {
        const string page = "<script>window.socket = new SocketIOClientProxy({ socketHost: 'wss://socket12.donationalerts.com:443', widgetToken: token })</script>";

        Assert.Equal("wss://socket12.donationalerts.com:443", DaMediaFeed.SocketHostOf(page));
        Assert.Null(DaMediaFeed.SocketHostOf("<html>no socket here</html>"));
    }

    [Fact]
    public void A_donation_event_with_a_video_gives_the_donation_number_and_the_video()
    {
        var donation = DaMediaFeed.DonationOf(Captured)!.Value;

        Assert.Equal(192395751, donation.Id);
        Assert.Equal(new DonationVideo("UQWK8jtYlnU", "WHEN DBD IS NOT SO COMPETITIVE | COMPILATION", "https://www.youtube.com/watch?v=UQWK8jtYlnU"), donation.Video);
    }

    [Theory]
    [InlineData("42[\"donation\",\"{\\\"id\\\":\\\"55\\\",\\\"additional_data\\\":\\\"{\\\\\\\"randomness\\\\\\\":1}\\\"}\"]", 55L)]
    [InlineData("42[\"donation\",\"{\\\"id\\\":56,\\\"additional_data\\\":null}\"]", 56L)]
    public void A_donation_without_a_video_gives_only_its_number(string packet, long id)
    {
        var donation = DaMediaFeed.DonationOf(packet)!.Value;

        Assert.Equal(id, donation.Id);
        Assert.Null(donation.Video);
    }

    [Theory]
    [InlineData("42[\"alert-show\",\"{\\\"action\\\":\\\"start\\\",\\\"alert_id\\\":\\\"192395751\\\"}\"]")]
    [InlineData("42[\"media\",\"{\\\"action\\\":\\\"get-current-media\\\",\\\"source\\\":\\\"last_alerts_widget\\\"}\"]")]
    [InlineData("42[\"donation\",\"not json\"]")]
    [InlineData("3")]
    [InlineData("40")]
    [InlineData("42[\"donation\",\"{\\\"id\\\":1,\\\"additional_data\\\":\\\"{\\\\\\\"media_data\\\\\\\":{\\\\\\\"url\\\\\\\":\\\\\\\"javascript:alert(1)\\\\\\\"}}\\\"}\"]")]
    public void Other_events_and_broken_ones_give_no_video(string packet)
    {
        Assert.Null(DaMediaFeed.DonationOf(packet)?.Video);
    }

    [Fact]
    public void A_video_is_kept_with_the_donation_and_a_donation_without_one_writes_nothing_extra()
    {
        var with = new Donation { Id = 7, Username = "viewer", Amount = 200, Currency = "RUB" };
        with.AttachVideo("UQWK8jtYlnU", "Ролик", "https://www.youtube.com/watch?v=UQWK8jtYlnU");
        var without = new Donation { Id = 8, Username = "other", Amount = 100, Currency = "RUB" };

        var back = JsonSerializer.Deserialize<Donation>(JsonSerializer.Serialize(with))!;

        Assert.True(back.HasVideo);
        Assert.Equal(("UQWK8jtYlnU", "Ролик", "https://www.youtube.com/watch?v=UQWK8jtYlnU"), (back.VideoId, back.VideoTitle, back.VideoUrl));
        Assert.Equal("https://i.ytimg.com/vi/UQWK8jtYlnU/mqdefault.jpg", back.VideoThumbnail);
        Assert.Equal("Видео: Ролик", back.VideoName);
        Assert.DoesNotContain("Video", JsonSerializer.Serialize(without));
        Assert.False(without.HasVideo);
    }

    [Fact]
    public void The_toast_names_the_ordered_video()
    {
        var silent = new Donation { Username = "viewer", Amount = 200, Currency = "RUB" };
        silent.AttachVideo("x", "Ролик", "https://www.youtube.com/watch?v=x");
        var talking = new Donation { Username = "viewer", Amount = 200, Currency = "RUB", Message = "смотри" };
        talking.AttachVideo("x", "Ролик", "https://www.youtube.com/watch?v=x");

        Assert.Equal("▶ Ролик", EventToasts.Donations(new[] { silent }).Message);
        Assert.Equal("смотри\n▶ Ролик", EventToasts.Donations(new[] { talking }).Message);
        Assert.Equal("viewer · 200 ₽", EventToasts.Donations(new[] { silent }).Line);
    }

    [Fact]
    public async Task The_feed_joins_the_widget_channel_with_its_key_and_reports_the_video_of_a_donation()
    {
        using var widget = new FakeWidget(socket => Send(socket, Captured));
        var feed = new DaMediaFeed(new HttpClient(), () => "https://www.donationalerts.com/widget/alerts?group_id=1&token=SECRET42", widget.Site);
        var found = Found(feed);
        feed.Start();
        try
        {
            Assert.Equal("/widget/alerts?group_id=1&token=SECRET42", await widget.PageAsked.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("42[\"add-user\",{\"token\":\"SECRET42\"}]", await widget.Joined.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var (id, video) = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(192395751, id);
            Assert.Equal("UQWK8jtYlnU", video.VideoId);
            Assert.Equal(SyncState.Ok, feed.Status.State);
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task A_russian_title_split_between_two_pieces_of_a_message_comes_out_whole()
    {
        const string title = "Обзор ролика";
        var bytes = Encoding.UTF8.GetBytes(Captured.Replace("WHEN DBD IS NOT SO COMPETITIVE | COMPILATION", title));
        var cut = Array.IndexOf(bytes, Encoding.UTF8.GetBytes("О")[0]) + 1;
        using var widget = new FakeWidget(async socket =>
        {
            await socket.SendAsync(bytes.AsMemory(0, cut), WebSocketMessageType.Text, false, CancellationToken.None);
            await socket.SendAsync(bytes.AsMemory(cut), WebSocketMessageType.Text, true, CancellationToken.None);
        });
        var feed = new DaMediaFeed(new HttpClient(), () => "https://www.donationalerts.com/widget/alerts?token=k", widget.Site);
        var found = Found(feed);
        feed.Start();
        try
        {
            var (_, video) = await found.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(title, video.Title);
        }
        finally
        {
            feed.Stop();
        }
    }

    [Fact]
    public async Task Removing_the_link_while_connected_stops_the_feed_and_says_so()
    {
        using var widget = new FakeWidget(_ => Task.CompletedTask);
        var link = "https://www.donationalerts.com/widget/alerts?token=k";
        var feed = new DaMediaFeed(new HttpClient(), () => link, widget.Site);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.StatusChanged += status =>
        {
            if (status.State == SyncState.Ok) connected.TrySetResult();
        };
        feed.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        link = "";
        feed.Start();
        await Task.Delay(500);

        Assert.Equal(SyncState.NotConnected, feed.Status.State);
        Assert.Equal("Ссылка виджета не задана.", feed.Status.Message);
    }

    [Fact]
    public void Without_a_link_the_feed_stays_off()
    {
        var feed = new DaMediaFeed(new HttpClient(), () => "");
        feed.Start();

        Assert.Equal(SyncState.NotConnected, feed.Status.State);
        Assert.Equal("Ссылка виджета не задана.", feed.Status.Message);
    }

    private static TaskCompletionSource<(long, DonationVideo)> Found(DaMediaFeed feed)
    {
        var found = new TaskCompletionSource<(long, DonationVideo)>(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.VideoFound += (id, video) => found.TrySetResult((id, video));
        return found;
    }

    internal static Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    internal sealed class FakeWidget : IDisposable
    {
        private readonly HttpListener _server = new();

        public FakeWidget(Func<WebSocket, Task> afterJoin)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Site = $"http://127.0.0.1:{port}";
            _server.Prefixes.Add(Site + "/");
            _server.Start();
            _ = Task.Run(async () =>
            {
                while (_server.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _server.GetContextAsync(); } catch { break; }
                    if (!context.Request.IsWebSocketRequest)
                    {
                        PageAsked.TrySetResult(context.Request.Url!.PathAndQuery);
                        var page = Encoding.UTF8.GetBytes($"<script>new SocketIOClientProxy({{ socketHost: 'ws://127.0.0.1:{port}', widgetToken: token }})</script>");
                        context.Response.ContentType = "text/html";
                        await context.Response.OutputStream.WriteAsync(page);
                        context.Response.Close();
                        continue;
                    }
                    _ = Task.Run(() => ServeAsync(context, afterJoin));
                }
            });
        }

        public static string ChannelOf(string site) => "ws" + site[4..] + "/socket.io/";

        private async Task ServeAsync(HttpListenerContext context, Func<WebSocket, Task> afterJoin)
        {
            try
            {
                var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                await Send(socket, "0{\"sid\":\"s1\",\"upgrades\":[],\"pingInterval\":25000,\"pingTimeout\":60000}");
                await Send(socket, "40");
                var buffer = new byte[4096];
                var first = await socket.ReceiveAsync(buffer, CancellationToken.None);
                Joined.TrySetResult(Encoding.UTF8.GetString(buffer, 0, first.Count));
                await afterJoin(socket);
                while (socket.State == WebSocketState.Open) await socket.ReceiveAsync(buffer, CancellationToken.None);
            }
            catch
            {
            }
        }

        public string Site { get; }

        public TaskCompletionSource<string> PageAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose() => _server.Stop();
    }
}
