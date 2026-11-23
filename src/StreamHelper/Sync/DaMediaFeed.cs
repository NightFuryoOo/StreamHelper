using System;
using System.Collections.Generic;
using System.Globalization;
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

public sealed record DaAlert(long Id, string Type, DateTime UntilUtc);

public sealed record DaAlertShow(string Action, long Id, string Type, int DurationMs);

public sealed record DaCommandResult(bool Success, string Message);

public sealed record DaAlertEvent(long Id, string Type, string Username, string Name, string Context, int Months, bool IsGift, bool Shown);

public sealed class DaMediaFeed : ISyncWorker, IDaAlertChannel
{
    public const string DonationAlertType = "1";
    public const string SubAlertType = "4";
    public const string GiftAlertType = "13";
    public const string NoLink = "Ссылка виджета DA не задана: вставь её в «Подключения» → DonationAlerts → «Заказанные видео».";
    public const string NoChannel = "Нет связи с виджетом DA, попробуй через несколько секунд.";
    public const string NothingShowing = "Сейчас оповещение DA не идёт.";
    public const string Skipped = "Оповещение пропущено.";
    public const string NotConfirmed = "DA не подтвердил пропуск.";
    public const string Repeated = "Оповещение отправлено на повтор.";
    public const string RepeatForbidden = "DA сейчас не разрешает повтор из программы (HTTP 403). Повтори его в панели DA «Последние сообщения».";

    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LastRetry = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AlertGrace = TimeSpan.FromSeconds(30);
    private static readonly Regex HostInPage = new(@"socketHost\s*:\s*'([^']+)'", RegexOptions.CultureInvariant);
    private const string DaSite = "https://www.donationalerts.com";

    private readonly HttpClient _http;
    private readonly Func<string> _link;
    private readonly string? _site;
    private readonly object _gate = new();
    private readonly Dictionary<long, TaskCompletionSource> _skipWaits = new();
    private CancellationTokenSource? _cts;
    private Func<string, Task>? _send;
    private string _channelToken = "";
    private DaAlert? _current;

    internal TimeSpan SkipConfirmWait { get; set; } = TimeSpan.FromSeconds(4);

    internal TimeSpan RepeatRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public event Action? AlertChanged;

    public DaAlert? CurrentAlert
    {
        get
        {
            lock (_gate)
            {
                return _current is { } alert && alert.UntilUtc > DateTime.UtcNow ? alert : null;
            }
        }
    }

    public bool IsShowing(long donationId) => IsShowing(donationId, DonationAlertType);

    public bool IsShowing(long alertId, string alertType) => CurrentAlert is { } alert && alert.Id == alertId && alert.Type == alertType;

    public event Action<DaAlertEvent>? AlertSeen;

    public event Action<DaAlertEvent>? AlertCame;

    public event Action<DaAlertShow>? AlertShown;

    public bool IsConnected => Volatile.Read(ref _send) != null;

    public static DaAlertEvent? AlertEventOf(string packet)
    {
        if (!packet.StartsWith("42", StringComparison.Ordinal)) return null;
        try
        {
            using var frame = JsonDocument.Parse(packet[2..]);
            var parts = frame.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() < 2 || parts[0].GetString() != "donation") return null;
            var body = parts[1].ValueKind == JsonValueKind.String ? parts[1].GetString() ?? "" : parts[1].GetRawText();
            using var alert = JsonDocument.Parse(body);
            var root = alert.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Number(root, "id") is not { } id) return null;
            var type = root.TryGetProperty("alert_type", out var kind) ? kind.ValueKind == JsonValueKind.Number ? kind.GetRawText() : kind.GetString() ?? "" : "";
            var shown = root.TryGetProperty("is_shown", out var flag) && (flag.ValueKind == JsonValueKind.Number ? flag.GetRawText() == "1" : flag.GetString() == "1");
            var name = "";
            var context = "";
            var months = 0;
            var gift = false;
            if (root.TryGetProperty("additional_data", out var extra) && (extra.ValueKind == JsonValueKind.String ? extra.GetString() : extra.ValueKind == JsonValueKind.Object ? extra.GetRawText() : null) is { Length: > 0 } text)
            {
                using var data = JsonDocument.Parse(text);
                if (data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("event_data", out var details) && details.ValueKind == JsonValueKind.Object)
                {
                    name = Text(details, "display_name");
                    if (name.Length == 0) name = Text(details, "username");
                    context = Text(details, "context");
                    months = (int)(Number(details, "cumulative_months") ?? 0);
                    gift = details.TryGetProperty("is_gift", out var isGift) && isGift.ValueKind == JsonValueKind.True;
                }
            }
            return new DaAlertEvent(id, type, Text(root, "username"), name, context, months, gift, shown);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    public static DaAlertShow? AlertShowOf(string packet)
    {
        if (!packet.StartsWith("42", StringComparison.Ordinal)) return null;
        try
        {
            using var frame = JsonDocument.Parse(packet[2..]);
            var parts = frame.RootElement;
            if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() < 2 || parts[0].GetString() != "alert-show") return null;
            var body = parts[1].ValueKind == JsonValueKind.String ? parts[1].GetString() ?? "" : parts[1].GetRawText();
            using var show = JsonDocument.Parse(body);
            var root = show.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var action = Text(root, "action");
            var id = Number(root, "alert_id");
            if (action.Length == 0 || id is not { } alertId) return null;
            var type = root.TryGetProperty("alert_type", out var kind) ? kind.ValueKind == JsonValueKind.Number ? kind.GetRawText() : kind.GetString() ?? "" : "";
            return new DaAlertShow(action, alertId, type, (int)(Number(root, "duration") ?? 0));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public static DaCommandResult ApiAnswer(string body)
    {
        var text = body.Trim();
        var open = text.IndexOf('(');
        if (open >= 0 && open < 64 && text.EndsWith(")", StringComparison.Ordinal)) text = text[(open + 1)..^1];
        else if (open >= 0 && open < 64 && text.EndsWith(");", StringComparison.Ordinal)) text = text[(open + 1)..^2];
        try
        {
            using var answer = JsonDocument.Parse(text);
            var root = answer.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new DaCommandResult(false, "DA ответил непонятно.");
            if (Text(root, "status") == "success") return new DaCommandResult(true, "");
            var message = Text(root, "message");
            return new DaCommandResult(false, message.Length > 0 ? "DA отказал: " + message : "DA отказал.");
        }
        catch (JsonException)
        {
            return new DaCommandResult(false, "DA ответил непонятно.");
        }
    }

    private static long? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number) ? number : null;
    }

    public async Task<DaCommandResult> SkipCurrentAsync()
    {
        if (TokenOf(_link()) == null) return new DaCommandResult(false, NoLink);
        if (Volatile.Read(ref _send) == null) return new DaCommandResult(false, NoChannel);
        return CurrentAlert is { } alert ? await SkipAsync(alert.Id, alert.Type) : new DaCommandResult(false, NothingShowing);
    }

    public async Task<DaCommandResult> SkipAsync(long alertId, string alertType)
    {
        if (TokenOf(_link()) == null) return new DaCommandResult(false, NoLink);
        Func<string, Task>? send;
        string token;
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            send = _send;
            token = _channelToken;
            if (send != null) _skipWaits[alertId] = confirmed;
        }
        if (send == null) return new DaCommandResult(false, NoChannel);
        try
        {
            var command = new { token, message_data = new { action = "skip", alert_id = alertId.ToString(CultureInfo.InvariantCulture), alert_type = alertType } };
            await send("42" + JsonSerializer.Serialize(new object[] { "alert-show", command }));
            var done = await Task.WhenAny(confirmed.Task, Task.Delay(SkipConfirmWait)) == confirmed.Task;
            if (!done) Log.Write($"DA widget: skipping alert {alertId} was not confirmed.");
            return done ? new DaCommandResult(true, Skipped) : new DaCommandResult(false, NotConfirmed);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Write("DA widget: skipping an alert failed: " + ex.Message);
            return new DaCommandResult(false, NoChannel);
        }
        finally
        {
            lock (_gate)
            {
                if (_skipWaits.TryGetValue(alertId, out var waiting) && waiting == confirmed) _skipWaits.Remove(alertId);
            }
        }
    }

    public async Task<DaCommandResult> RepeatAsync(long alertId, string alertType = DonationAlertType)
    {
        var token = TokenOf(_link());
        if (token == null) return new DaCommandResult(false, NoLink);
        var url = $"{_site ?? DaSite}/api/repeatalert?alert={alertId.ToString(CultureInfo.InvariantCulture)}&alert_type={Uri.EscapeDataString(alertType)}&token={Uri.EscapeDataString(token)}";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var response = await _http.GetAsync(url, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Write($"DA widget: repeating alert {alertId} answered HTTP {(int)response.StatusCode}.");
                    return new DaCommandResult(false, response.StatusCode == System.Net.HttpStatusCode.Forbidden
                        ? RepeatForbidden
                        : $"DA ответил HTTP {(int)response.StatusCode}.");
                }
                var answer = ApiAnswer(body);
                if (!answer.Success) Log.Write($"DA widget: repeating alert {alertId} refused: {answer.Message}");
                return answer.Success ? new DaCommandResult(true, Repeated) : answer;
            }
            catch (HttpRequestException ex) when (attempt < 3 && ex.HttpRequestError is HttpRequestError.SecureConnectionError or HttpRequestError.ConnectionError)
            {
                await Task.Delay(RepeatRetryDelay);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                Log.Write($"DA widget: repeating alert {alertId} failed: {ex.Message}");
                return new DaCommandResult(false, ex is OperationCanceledException ? "DA не ответил вовремя." : "Нет связи с DA: " + ex.Message);
            }
        }
    }

    private void OnAlertShow(DaAlertShow show)
    {
        var changed = false;
        TaskCompletionSource? skipped = null;
        lock (_gate)
        {
            if (show.Action == "start")
            {
                _current = new DaAlert(show.Id, show.Type, DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(0, show.DurationMs)) + AlertGrace);
                changed = true;
            }
            else if (show.Action is "end" or "skip")
            {
                if (_current?.Id == show.Id)
                {
                    _current = null;
                    changed = true;
                }
                if (show.Action == "skip" && _skipWaits.Remove(show.Id, out var waiting)) skipped = waiting;
            }
        }
        skipped?.TrySetResult();
        if (changed) AlertChanged?.Invoke();
    }

    private void ForgetChannel()
    {
        bool changed;
        lock (_gate)
        {
            _send = null;
            _channelToken = "";
            changed = _current != null;
            _current = null;
        }
        if (changed) AlertChanged?.Invoke();
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
                    lock (_gate)
                    {
                        _send = SendAsync;
                        _channelToken = token;
                    }
                    SetStatus(SyncState.Ok, "Виджет DA подключён: видно видео из донатов, работают повтор и пропуск оповещений.");
                }
                else if (AlertShowOf(text) is { } show)
                {
                    OnAlertShow(show);
                    AlertShown?.Invoke(show);
                }
                else if (AlertEventOf(text) is { } came)
                {
                    AlertCame?.Invoke(came);
                    if (came.Type != DonationAlertType)
                    {
                        if (!came.Shown) Log.Write($"DA widget: alert of type {came.Type}{(came.Context.Length > 0 ? " (" + came.Context + ")" : "")} came.");
                        AlertSeen?.Invoke(came);
                    }
                    else if (DonationOf(text) is { Video: { } video } donation)
                    {
                        Log.Write($"DA widget: donation {donation.Id} came with a video.");
                        VideoFound?.Invoke(donation.Id, video);
                    }
                }
            }
        }
        finally
        {
            ForgetChannel();
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
