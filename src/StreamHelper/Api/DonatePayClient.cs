using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Api;

public interface IDonationWindowSource
{
    Task<IReadOnlyList<Donation>> GetRecentAsync(CancellationToken ct);
}

public sealed class DonatePayClient : IDonationWindowSource
{
    public const string DefaultBaseUrl = "https://donatepay.ru";

    private static readonly TimeSpan Moscow = TimeSpan.FromHours(3);

    private readonly SettingsStore _store;
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public DonatePayClient(SettingsStore store, HttpClient http, string? baseUrl = null)
    {
        _store = store;
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<Donation>> GetRecentAsync(CancellationToken ct)
    {
        var key = _store.Current.DonatePayKey;
        if (key.Length == 0) throw new AuthRequiredException("DonatePay ещё не подключён.");

        var url = $"{_baseUrl}/api/v1/transactions?access_token={Uri.EscapeDataString(key)}&limit=100&order=DESC&type=donation&status=success";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthRequiredException("DonatePay отклонил ключ API. Проверь ключ в настройках, вкладка «Донаты».");
        }
        if (response.StatusCode == (HttpStatusCode)429)
        {
            throw new HttpRequestException("DonatePay: слишком много запросов (HTTP 429).");
        }
        if (!LooksLikeJson(body))
        {
            throw new HttpRequestException($"DonatePay: HTTP {(int)response.StatusCode}.");
        }
        return ParseTransactions(body);
    }

    public static IReadOnlyList<Donation> ParseTransactions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<Donation>();

        var status = ReadString(root, "status");
        if (status.Length > 0 && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            var message = ReadString(root, "message");
            if (IsAuthProblem(message))
            {
                throw new AuthRequiredException("DonatePay отклонил ключ API. Проверь ключ в настройках, вкладка «Донаты».");
            }
            throw new HttpRequestException(message.Length > 0 ? "DonatePay: " + message : "DonatePay вернул ошибку.");
        }

        var items = new List<Donation>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return items;
        foreach (var element in data.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (!TryReadId(element, out var id)) continue;
            var type = ReadString(element, "type");
            if (type.Length > 0 && !type.Equals("donation", StringComparison.OrdinalIgnoreCase)) continue;
            var itemStatus = ReadString(element, "status");
            if (itemStatus.Length > 0 && !itemStatus.Equals("success", StringComparison.OrdinalIgnoreCase)) continue;

            var vars = element.TryGetProperty("vars", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
            var name = vars.ValueKind == JsonValueKind.Object ? ReadString(vars, "name") : "";
            var message = vars.ValueKind == JsonValueKind.Object ? ReadString(vars, "comment") : "";
            if (message.Length == 0) message = ReadString(element, "comment");

            var currency = ReadString(element, "currency");
            items.Add(new Donation
            {
                Source = DonationSources.DonatePay,
                Id = id,
                Username = name,
                Message = message,
                Amount = ReadDecimal(element, "sum"),
                Currency = currency.Length > 0 ? currency.ToUpperInvariant() : "RUB",
                CreatedAtUtc = ReadCreatedAt(element),
            });
        }
        return items;
    }

    internal static DateTime ToUtc(string date, string? timezone)
    {
        if (string.IsNullOrWhiteSpace(date)) return default;
        if (Regex.IsMatch(date.Trim(), @"(Z|[+-]\d{2}:?\d{2})$") &&
            DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamped))
        {
            return stamped.UtcDateTime;
        }
        if (!DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return default;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        TimeSpan offset;
        if (timezone != null && Regex.IsMatch(timezone, @"^[+-]\d{2}:?\d{2}$"))
        {
            var sign = timezone[0] == '-' ? -1 : 1;
            var digits = timezone[1..].Replace(":", "");
            offset = new TimeSpan(sign * int.Parse(digits[..2], CultureInfo.InvariantCulture), sign * int.Parse(digits[2..], CultureInfo.InvariantCulture), 0);
        }
        else if (TryFindZone(timezone, out var zone))
        {
            offset = zone.GetUtcOffset(local);
        }
        else
        {
            offset = Moscow;
        }
        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    private static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static DateTime ReadCreatedAt(JsonElement element)
    {
        if (!element.TryGetProperty("created_at", out var value)) return default;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return ToUtc(ReadString(value, "date"), ReadString(value, "timezone"));
            case JsonValueKind.String:
                return ToUtc(value.GetString() ?? "", null);
            case JsonValueKind.Number when value.TryGetInt64(out var seconds) && seconds > 0:
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            default:
                return default;
        }
    }

    private static bool IsAuthProblem(string message) =>
        message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unauthor", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("forbidden", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeJson(string body)
    {
        var text = body.AsSpan().TrimStart();
        return text.Length > 0 && (text[0] == '{' || text[0] == '[');
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        if (!element.TryGetProperty("id", out var value)) return false;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out id);
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
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
}
