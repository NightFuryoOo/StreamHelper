using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record DonationVideo(string VideoId, string Title, string Url);

public sealed class DaMediaFeed : ISyncWorker
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(60);
    private static readonly Regex HostInPage = new(@"socketHost\s*:\s*'([^']+)'", RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly Func<string> _link;
    private readonly string? _site;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public DaMediaFeed(HttpClient http, Func<string> link, string? site = null)
    {
        _http = http;
        _link = link;
        _site = string.IsNullOrWhiteSpace(site) ? null : site.TrimEnd('/');
    }

    public SyncStatus Status { get; private set; } = new(SyncState.NotConnected, "Ссылка виджета не задана.");

    public event Action<SyncStatus>? StatusChanged;

    public event Action<long, DonationVideo>? VideoFound;

    public void Start()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            var link = _link();
            if (TokenOf(link) == null)
            {
                SetStatus(SyncState.NotConnected, "Ссылка виджета не задана.");
                return;
            }
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => RunAsync(link, cts.Token));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public static string? TokenOf(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)) return null;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("token=", StringComparison.Ordinal)) return Uri.UnescapeDataString(pair[6..]) is { Length: > 0 } token ? token : null;
        }
        return null;
    }

    public static bool IsWidgetLink(string link) =>
        Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "donationalerts.com" || uri.Host.EndsWith(".donationalerts.com", StringComparison.Ordinal)) &&
        uri.AbsolutePath.StartsWith("/widget/", StringComparison.Ordinal) && TokenOf(link) != null;

    public static string? SocketHostOf(string page) => HostInPage.Match(page) is { Success: true } match ? match.Groups[1].Value : null;

    public static (long Id, DonationVideo? Video)? DonationOf(string packet)
    {
        if (!packet.StartsWith("42", StringComparison.Ordinal)) return null;
        try
        {
            using var frame = JsonDocument.Parse(packet[2..]);
            var parts = frame.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() < 2 || parts[0].GetString() != "donation") return null;
            var body = parts[1].ValueKind == JsonValueKind.String ? parts[1].GetString() ?? "" : parts[1].GetRawText();
            using var donation = JsonDocument.Parse(body);
            var root = donation.RootElement;
            if (!root.TryGetProperty("id", out var idElement)) return null;
            long id;
            if (idElement.ValueKind == JsonValueKind.Number) id = idElement.GetInt64();
            else if (!long.TryParse(idElement.GetString(), out id)) return null;
            return (id, VideoOf(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DonationVideo? VideoOf(JsonElement donation)
    {
        if (!donation.TryGetProperty("additional_data", out var extra)) return null;
        var text = extra.ValueKind == JsonValueKind.String ? extra.GetString() : extra.ValueKind == JsonValueKind.Object ? extra.GetRawText() : null;
        if (string.IsNullOrEmpty(text)) return null;
        using var data = JsonDocument.Parse(text);
        if (!data.RootElement.TryGetProperty("media_data", out var media) || media.ValueKind != JsonValueKind.Object) return null;
        var url = Text(media, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
        return new DonationVideo(Text(media, "id"), Text(media, "title"), uri.AbsoluteUri);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task RunAsync(string link, CancellationToken ct)
    {
        var wait = FirstRetry;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(SyncState.NotConnected, "Подключаюсь к виджету DA…");
                await ListenAsync(link, ct);
                wait = FirstRetry;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Write("DA widget feed failed: " + ex.Message);
                SetStatus(SyncState.Error, "Нет связи с виджетом DA: " + (ex is OperationCanceledException ? "виджет не ответил вовремя." : ex.Message));
            }
            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, LastRetry.Ticks));
        }
    }

    private async Task ListenAsync(string link, CancellationToken ct)
    {
        var token = TokenOf(link) ?? throw new InvalidOperationException("в ссылке нет ключа");
        var page = _site == null ? link : _site + new Uri(link).PathAndQuery;
        using var pageCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pageCts.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _http.GetAsync(page, pageCts.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"страница виджета ответила HTTP {(int)response.StatusCode}");
        var host = SocketHostOf(await response.Content.ReadAsStringAsync(pageCts.Token)) ?? throw new InvalidOperationException("на странице виджета нет адреса канала");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://www.donationalerts.com");
        await socket.ConnectAsync(new Uri(host.TrimEnd('/') + "/socket.io/?EIO=3&transport=websocket"), pageCts.Token);
        using var live = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var send = new SemaphoreSlim(1, 1);
        async Task SendAsync(string text)
        {
            await send.WaitAsync(live.Token);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, live.Token);
            }
            finally
            {
                send.Release();
            }
        }

        var pingEvery = TimeSpan.FromSeconds(25);
        var pinger = Task.Run(async () =>
        {
            while (!live.IsCancellationRequested)
            {
                await Task.Delay(pingEvery, live.Token);
                await SendAsync("2");
            }
        }, live.Token);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await WebSocketText.ReceiveAsync(socket, pingEvery + TimeSpan.FromSeconds(45),
                               "нет сообщений от виджета DA дольше таймаута, переподключаюсь.", "виджет DA прислал слишком большое сообщение.", ct)
                           ?? throw new WebSocketException("канал виджета закрыл соединение");
                if (text.StartsWith("0{", StringComparison.Ordinal))
                {
                    using var open = JsonDocument.Parse(text[1..]);
                    if (open.RootElement.TryGetProperty("pingInterval", out var interval) && interval.TryGetInt32(out var ms) && ms > 1000) pingEvery = TimeSpan.FromMilliseconds(ms);
                }
                else if (text == "40")
                {
                    await SendAsync("42" + JsonSerializer.Serialize(new object[] { "add-user", new { token } }));
                    SetStatus(SyncState.Ok, "Виджет DA подключён: видео из донатов будут видны.");
                }
                else if (DonationOf(text) is { Video: { } video } donation)
                {
                    Log.Write($"DA widget: donation {donation.Id} came with a video.");
                    VideoFound?.Invoke(donation.Id, video);
                }
            }
        }
        finally
        {
            live.Cancel();
            try { await pinger; } catch (OperationCanceledException) { } catch (WebSocketException) { }
        }
    }

    private void SetStatus(SyncState state, string message)
    {
        Status = new SyncStatus(state, message);
        StatusChanged?.Invoke(Status);
    }
}
