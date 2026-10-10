using System;
using System.Globalization;

namespace StreamHelper.Models;

public static class HexColor
{
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = text.Trim().TrimStart('#');
        if (digits.Length == 3) digits = string.Concat(digits[0], digits[0], digits[1], digits[1], digits[2], digits[2]);
        if (digits.Length != 6) return null;
        foreach (var c in digits)
        {
            if (!Uri.IsHexDigit(c)) return null;
        }
        return "#" + digits.ToUpperInvariant();
    }

    public static (byte R, byte G, byte B) Parse(string normalized) =>
        (byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
