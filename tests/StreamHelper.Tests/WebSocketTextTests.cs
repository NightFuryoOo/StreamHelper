using System.Net.WebSockets;
using System.Text;
using StreamHelper.Sync;
using static StreamHelper.Tests.DaMediaFeedTests;

namespace StreamHelper.Tests;

public class WebSocketTextTests
{
    [Fact]
    public async Task A_silent_channel_gives_the_given_russian_reason()
    {
        using var widget = new FakeWidget(_ => Task.CompletedTask);
        using var client = await ConnectAsync(widget, join: false);

        var error = await Assert.ThrowsAsync<TimeoutException>(() => ReadAsync(client, TimeSpan.FromMilliseconds(300)));

        Assert.Equal("тихо", error.Message);
    }

    [Fact]
    public async Task An_oversized_message_is_refused_with_the_given_reason()
    {
        using var widget = new FakeWidget(socket =>
            socket.SendAsync(Encoding.UTF8.GetBytes(new string('a', 1_100_000)), WebSocketMessageType.Text, true, CancellationToken.None));
        using var client = await ConnectAsync(widget, join: true);

        var error = await Assert.ThrowsAsync<IOException>(() => ReadAsync(client));

        Assert.Equal("много", error.Message);
    }

    [Fact]
    public async Task A_closed_channel_gives_nothing()
    {
        using var widget = new FakeWidget(socket => socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None));
        using var client = await ConnectAsync(widget, join: true);

        Assert.Null(await ReadAsync(client));
    }

    private static async Task<ClientWebSocket> ConnectAsync(FakeWidget widget, bool join)
    {
        var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri(FakeWidget.ChannelOf(widget.Site)), CancellationToken.None);
        Assert.StartsWith("0{", await ReadAsync(client));
        Assert.Equal("40", await ReadAsync(client));
        if (join) await Send(client, "42[\"add-user\"]");
        return client;
    }

    private static Task<string?> ReadAsync(WebSocket socket, TimeSpan? timeout = null) =>
        WebSocketText.ReceiveAsync(socket, timeout ?? TimeSpan.FromSeconds(10), "тихо", "много", CancellationToken.None);
}
