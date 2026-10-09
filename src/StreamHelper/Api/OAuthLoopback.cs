using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamHelper.Api;

public static class OAuthLoopback
{
    private const string DonePage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>StreamHelper</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;background:#1b1d23;color:#e6e8ee;text-align:center;padding-top:20vh\">" +
        "<h2>Готово</h2><p>DonationAlerts подключён. Эту вкладку можно закрыть.</p></body></html>";

    private const string FailPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>StreamHelper</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;background:#1b1d23;color:#e6e8ee;text-align:center;padding-top:20vh\">" +
        "<h2>Не получилось</h2><p>Вернись в StreamHelper и попробуй ещё раз.</p></body></html>";

    public static async Task<string> WaitForCodeAsync(int port, string expectedState, Action openBrowser, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось занять порт {port} для ответа DonationAlerts: {ex.Message}. Закрой программу, которая его использует, и попробуй ещё раз.", ex);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        });

        openBrowser();

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            var query = context.Request.QueryString;
            var path = context.Request.Url?.AbsolutePath ?? "";
            if (!path.Equals("/callback", StringComparison.OrdinalIgnoreCase))
            {
                await RespondAsync(context, 404, FailPage);
                continue;
            }

            var error = query["error"];
            var code = query["code"];
            var state = query["state"];

            if (!string.IsNullOrEmpty(error))
            {
                await RespondAsync(context, 400, FailPage);
                throw new InvalidOperationException($"DonationAlerts вернул ошибку: {error} {query["error_description"]}".Trim());
            }
            if (string.IsNullOrEmpty(code) || (!string.IsNullOrEmpty(state) && state != expectedState))
            {
                await RespondAsync(context, 400, FailPage);
                continue;
            }

            await RespondAsync(context, 200, DonePage);
            return code;
        }
    }

    private const string RelayPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>StreamHelper</title></head>" +
        "<body style=\"font-family:Segoe UI,sans-serif;background:#1b1d23;color:#e6e8ee;text-align:center;padding-top:20vh\">" +
        "<h2 id=\"t\">Подключаю…</h2><p id=\"m\"></p>" +
        "<script>(function(){" +
        "var h=location.hash?location.hash.substring(1):'';" +
        "function show(t,m){document.getElementById('t').textContent=t;document.getElementById('m').textContent=m;}" +
        "if(!h){show('Не получилось','Токен не пришёл. Вернись в StreamHelper и попробуй ещё раз.');return;}" +
        "var x=new XMLHttpRequest();x.open('POST','/token');x.setRequestHeader('Content-Type','text/plain');" +
        "x.onload=function(){if(x.status===200){show('Готово','DonationAlerts подключён. Эту вкладку можно закрыть.');}" +
        "else{show('Не получилось','Вернись в StreamHelper и попробуй ещё раз.');}};" +
        "x.onerror=function(){show('Не получилось','Нет связи с StreamHelper. Он запущен?');};" +
        "x.send(h);history.replaceState(null,'',location.pathname);" +
        "})();</script></body></html>";

    public sealed record ImplicitToken(string AccessToken, long? ExpiresInSeconds);

    public static async Task<ImplicitToken> WaitForTokenAsync(int port, string expectedState, Action openBrowser, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Не удалось занять порт {port} для ответа DonationAlerts: {ex.Message}. Закрой программу, которая его использует, и попробуй ещё раз.", ex);
        }

        using var registration = ct.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        });

        var origin = $"http://127.0.0.1:{port}";
        openBrowser();

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "";

            if (request.HttpMethod == "GET" && path.Equals("/callback", StringComparison.OrdinalIgnoreCase))
            {
                var queryError = request.QueryString["error"];
                if (!string.IsNullOrEmpty(queryError))
                {
                    await RespondAsync(context, 400, FailPage);
                    throw new InvalidOperationException($"DonationAlerts вернул ошибку: {queryError} {request.QueryString["error_description"]}".Trim());
                }
                await RespondAsync(context, 200, RelayPage);
                continue;
            }

            if (request.HttpMethod == "POST" && path.Equals("/token", StringComparison.OrdinalIgnoreCase))
            {
                var requestOrigin = request.Headers["Origin"];
                if (!string.IsNullOrEmpty(requestOrigin) && !string.Equals(requestOrigin, origin, StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 403, FailPage);
                    continue;
                }

                string body;
                using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = await reader.ReadToEndAsync();
                }
                var form = System.Web.HttpUtility.ParseQueryString(body);

                var error = form["error"];
                if (!string.IsNullOrEmpty(error))
                {
                    await RespondAsync(context, 400, FailPage);
                    throw new InvalidOperationException($"DonationAlerts вернул ошибку: {error} {form["error_description"]}".Trim());
                }

                var token = form["access_token"];
                if (string.IsNullOrEmpty(token) || form["state"] != expectedState)
                {
                    await RespondAsync(context, 400, FailPage);
                    continue;
                }

                long? expires = long.TryParse(form["expires_in"], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
                await RespondAsync(context, 200, DonePage);
                return new ImplicitToken(token, expires);
            }

            await RespondAsync(context, 404, FailPage);
        }
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
