using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Api;

public sealed class DonateXClient : IDonationWindowSource
{
    public const string DefaultBaseUrl = "https://donatex.gg";

    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public DonateXClient(SettingsStore store, HttpClient http, string? baseUrl = null)
    {
        _store = store;
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<Donation>> GetRecentAsync(CancellationToken ct)
    {
        var token = _store.Current.DonateXToken;
        if (token.Length == 0) throw new AuthRequiredException("DonateX ещё не подключён.");

        var url = $"{_baseUrl}/api/v1/donations?skip=0&take=100&sortOrder=NewestFirst&token={Uri.EscapeDataString(token)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthRequiredException("DonateX отклонил токен. Проверь токен в настройках, вкладка «Донаты».");
        }
        if (response.StatusCode == (HttpStatusCode)429)
        {
            throw new HttpRequestException("DonateX: слишком много запросов (HTTP 429).");
        }
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"DonateX: HTTP {(int)response.StatusCode}.");
        }
        return ParseDonations(await response.Content.ReadAsStringAsync(ct));
    }

    public static IReadOnlyList<Donation> ParseDonations(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var array = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            array = default;
            foreach (var name in new[] { "data", "items", "donations" })
            {
                if (root.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Array)
                {
                    array = inner;
                    break;
                }
            }
        }
        var items = new List<Donation>();
        if (array.ValueKind != JsonValueKind.Array) return items;

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var id = ReadString(element, "id");
            if (id.Length == 0) continue;

            var at = ReadTimestamp(element, "timestamp");
            var message = ReadString(element, "message");
            var isTest = element.TryGetProperty("isTest", out var test) && test.ValueKind == JsonValueKind.True;
            if (isTest) message = message.Length == 0 ? "[тест]" : "[тест] " + message;

            var currency = ReadString(element, "currency");
            items.Add(new Donation
            {
                Source = DonationSources.DonateX,
                ExternalId = id,
                Id = at.Ticks,
                Username = ReadString(element, "username"),
                Message = message,
                Amount = ReadDecimal(element, "amount"),
                Currency = currency.Length > 0 ? currency.ToUpperInvariant() : "RUB",
                CreatedAtUtc = at,
            });
        }
        return items;
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => "",
        };
    }

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return 0;
    }

    private static DateTime ReadTimestamp(JsonElement element, string name) =>
        DateTimeOffset.TryParse(ReadString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : default;
}
