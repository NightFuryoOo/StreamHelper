using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Sync;

public static class ColorQuantizer
{
    public static (byte[] Indices, Rgba[] Palette) Quantize(byte[] rgba, int maxColors)
    {
        if (maxColors < 2) throw new ArgumentOutOfRangeException(nameof(maxColors));
        var pixelCount = rgba.Length / 4;

        var counts = new Dictionary<uint, int>();
        var keys = new uint[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var a = rgba[i * 4 + 3];
            var key = a == 0 ? 0u : Pack(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], a);
            keys[i] = key;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }

        var hasTransparent = counts.ContainsKey(0u);
        var colours = counts.Where(c => c.Key != 0u).Select(c => (Color: Unpack(c.Key), Count: c.Value)).ToList();
        var budget = maxColors - (hasTransparent ? 1 : 0);

        var palette = new List<Rgba>();
        if (hasTransparent) palette.Add(new Rgba(0, 0, 0, 0));
        if (colours.Count <= budget)
        {
            palette.AddRange(colours.Select(c => c.Color));
        }
        else
        {
            palette.AddRange(MedianCut(colours, budget));
        }

        var lookup = new Dictionary<uint, byte>();
        var indices = new byte[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            if (!lookup.TryGetValue(keys[i], out var index))
            {
                index = keys[i] == 0u ? (byte)0 : Nearest(palette, Unpack(keys[i]), hasTransparent ? 1 : 0);
                lookup[keys[i]] = index;
            }
            indices[i] = index;
        }
        return (indices, palette.ToArray());
    }

    private static List<Rgba> MedianCut(List<(Rgba Color, int Count)> colours, int boxCount)
    {
        var boxes = new List<List<(Rgba Color, int Count)>> { colours };
        while (boxes.Count < boxCount)
        {
            var target = -1;
            double bestScore = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                var score = (double)boxes[i].Sum(c => c.Count) * Range(boxes[i], out _);
                if (score > bestScore)
                {
                    bestScore = score;
                    target = i;
                }
            }
            if (target < 0) break;

            var box = boxes[target];
            Range(box, out var channel);
            var sorted = box.OrderBy(c => Channel(c.Color, channel)).ToList();
            var half = sorted.Sum(c => c.Count) / 2;
            var running = 0;
            var split = 1;
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                running += sorted[i].Count;
                split = i + 1;
                if (running >= half) break;
            }
            boxes[target] = sorted.Take(split).ToList();
            boxes.Add(sorted.Skip(split).ToList());
        }
        return boxes.Select(Average).ToList();
    }

    private static int Range(List<(Rgba Color, int Count)> box, out int channel)
    {
        var best = -1;
        channel = 0;
        for (var c = 0; c < 4; c++)
        {
            var min = 255;
            var max = 0;
            foreach (var (color, _) in box)
            {
                var v = Channel(color, c);
                if (v < min) min = v;
                if (v > max) max = v;
            }
            if (max - min > best)
            {
                best = max - min;
                channel = c;
            }
        }
        return best;
    }

    private static Rgba Average(List<(Rgba Color, int Count)> box)
    {
        double total = 0, weightTotal = 0, r = 0, g = 0, b = 0, a = 0;
        foreach (var (color, count) in box)
        {
            var weight = count * (color.A / 255.0 + 0.01);
            r += color.R * weight;
            g += color.G * weight;
            b += color.B * weight;
            weightTotal += weight;
            a += color.A * count;
            total += count;
        }
        return new Rgba((byte)Math.Round(r / weightTotal), (byte)Math.Round(g / weightTotal), (byte)Math.Round(b / weightTotal),
            (byte)Math.Max(1, Math.Round(a / total)));
    }

    private static byte Nearest(List<Rgba> palette, Rgba color, int first)
    {
        var best = first;
        var bestDistance = long.MaxValue;
        for (var i = first; i < palette.Count; i++)
        {
            long dr = palette[i].R - color.R, dg = palette[i].G - color.G, db = palette[i].B - color.B, da = palette[i].A - color.A;
            var distance = dr * dr + dg * dg + db * db + da * da * 2;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return (byte)best;
    }

    private static int Channel(Rgba c, int channel) => channel switch { 0 => c.R, 1 => c.G, 2 => c.B, _ => c.A };

    private static uint Pack(byte r, byte g, byte b, byte a) => (uint)(r << 24 | g << 16 | b << 8 | a);

    private static Rgba Unpack(uint key) => new((byte)(key >> 24), (byte)(key >> 16), (byte)(key >> 8), (byte)key);
}
