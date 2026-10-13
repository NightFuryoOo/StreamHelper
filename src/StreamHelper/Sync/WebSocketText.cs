using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Sync;

internal static class WebSocketText
{
    private const int MaxMessageBytes = 1_000_000;

    public static async Task<string?> ReceiveAsync(WebSocket socket, TimeSpan timeout, string silentMessage, string tooBigMessage, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeoutCts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaxMessageBytes) throw new IOException(tooBigMessage);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(silentMessage);
        }
    }
}
