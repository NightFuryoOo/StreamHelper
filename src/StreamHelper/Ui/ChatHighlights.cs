using System;
using System.Collections.Generic;
using System.Globalization;

namespace StreamHelper.Ui;

public static class ChatHighlights
{
    public sealed record Swatch(string Name, string Hex);

    public static readonly IReadOnlyList<Swatch> Palette = new[]
    {
        new Swatch("Жёлтый", "#FFD60A"),
        new Swatch("Оранжевый", "#FF9F0A"),
        new Swatch("Красный", "#FF453A"),
        new Swatch("Розовый", "#FF6BB5"),
        new Swatch("Фиолетовый", "#BF5AF2"),
        new Swatch("Синий", "#0A84FF"),
        new Swatch("Голубой", "#64D2FF"),
        new Swatch("Зелёный", "#30D158"),
    };

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

    public static (byte R, byte G, byte B) FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rr = r / 255.0, gg = g / 255.0, bb = b / 255.0;
        var max = Math.Max(rr, Math.Max(gg, bb));
        var min = Math.Min(rr, Math.Min(gg, bb));
        var d = max - min;
        double h;
        if (d == 0) h = 0;
        else if (max == rr) h = 60 * (((gg - bb) / d % 6 + 6) % 6);
        else if (max == gg) h = 60 * ((bb - rr) / d + 2);
        else h = 60 * ((rr - gg) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    public static (byte R, byte G, byte B) Parse(string normalized) =>
        (byte.Parse(normalized.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(normalized.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         byte.Parse(normalized.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}