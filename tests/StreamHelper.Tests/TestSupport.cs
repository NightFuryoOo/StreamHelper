using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Tests;

internal static class TestEnvironment
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void KeepLogsOutOfRealAppData() =>
        AppPaths.Init(Path.Combine(Path.GetTempPath(), "streamhelper-tests-logs"));
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "streamhelper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
        }
    }
}

internal sealed class FakeSource : IDonationSource
{
    private readonly Dictionary<int, DonationPage> _pages;

    public FakeSource(Dictionary<int, DonationPage> pages) => _pages = pages;

    public List<int> Requested { get; } = new();

    public Task<DonationPage> GetDonationsAsync(int page, CancellationToken ct)
    {
        Requested.Add(page);
        return Task.FromResult(_pages.TryGetValue(page, out var result) ? result : new DonationPage(Array.Empty<Donation>(), false, page, page));
    }
}

internal static class Make
{
    public static Donation D(long id, string user = "u") =>
        new() { Id = id, Username = user + id, Amount = 100, Currency = "RUB", CreatedAtUtc = DateTime.UtcNow };

    public static DonationPage Page(int current, int last, params long[] ids) =>
        new(ids.Select(id => D(id)).ToList(), current < last, current, last);

    public static string PageJson(int current, int last, params (long id, string user, string message)[] items)
    {
        var data = string.Join(",", items.Select(i =>
            $"{{\"id\":{i.id},\"name\":\"donation\",\"username\":\"{i.user}\",\"message_type\":\"text\",\"message\":\"{i.message}\"," +
            "\"amount\":500,\"currency\":\"RUB\",\"is_shown\":0,\"created_at\":\"2026-10-05 09:00:00\",\"shown_at\":null}"));
        var next = current < last ? "\"https://example.test/next\"" : "null";
        return $"{{\"data\":[{data}],\"links\":{{\"first\":null,\"last\":null,\"prev\":null,\"next\":{next}}}," +
               $"\"meta\":{{\"current_page\":{current},\"last_page\":{last},\"per_page\":30,\"total\":{items.Length}}}}}";
    }

    public static SettingsStore Settings(TempDir dir)
    {
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.ClientId = "client";
        store.Current.ClientSecret = "secret";
        store.Current.AccessToken = "access-1";
        store.Current.RefreshToken = "refresh-1";
        store.Current.AccessTokenExpiresUtc = DateTime.UtcNow.AddHours(1);
        return store;
    }
}

internal sealed class MockDa : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public MockDa()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Loop);
    }

    public string BaseUrl { get; }

    public Func<HttpListenerRequest, string, (int Status, string Body)> Handler { get; set; } = (_, _) => (404, "");

    public List<string> Requests { get; } = new();

    public DaEndpoints Endpoints => new(BaseUrl + "/oauth/authorize", BaseUrl + "/oauth/token", BaseUrl + "/api/v1/alerts/donations");

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }
            lock (Requests) Requests.Add($"{context.Request.HttpMethod} {context.Request.Url!.PathAndQuery} auth={context.Request.Headers["Authorization"]} body={body}");
            var (status, text) = Handler(context.Request, body);
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
    }
}
