using System;
using System.Globalization;

namespace StreamHelper.Ui;

internal static class ChatColors
{
    private static readonly string[] Defaults =
    {
        "#FF0000", "#0000FF", "#008000", "#B22222", "#FF7F50", "#9ACD32", "#FF4500", "#2E8B57",
        "#DAA520", "#D2691E", "#5F9EA0", "#1E90FF", "#FF69B4", "#8A2BE2", "#00FF7F",
    };

    private const double MinLuminance = 0.35;

    public static (byte R, byte G, byte B) Readable(string? hex, string login)
    {
        if (!TryParse(hex, out var rgb)) TryParse(Defaults[(int)(Hash(login) % (uint)Defaults.Length)], out rgb);

        var (r, g, b) = (rgb.R / 255.0, rgb.G / 255.0, rgb.B / 255.0);
        var mix = 0.0;
        while (Luminance(Mix(r, mix), Mix(g, mix), Mix(b, mix)) < MinLuminance && mix < 1) mix += 0.05;
        return (ToByte(Mix(r, mix)), ToByte(Mix(g, mix)), ToByte(Mix(b, mix)));
    }

    internal static bool TryParse(string? hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var text = hex.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;
        rgb = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static double Mix(double channel, double towardsWhite) => channel + (1 - channel) * towardsWhite;

    private static byte ToByte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);

    private static double Luminance(double r, double g, double b) => 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

    private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static uint Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text.ToLowerInvariant()) hash = (hash ^ ch) * 16777619u;
        return hash;
    }
}
