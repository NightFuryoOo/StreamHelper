using System;

namespace StreamHelper.Sync;

public sealed record EncodedIcon(byte[] Png, bool ColoursReduced, int Colours);

public static class IconEncoder
{
    private static readonly int[] PaletteSteps = { 256, 192, 128, 96, 64, 48, 32, 24, 16, 8 };

    public static EncodedIcon Encode(byte[] imageData, int size, int maxBytes)
    {
        var rgba = ImageSizing.ToSquareRgba(imageData, size);

        var full = PngWriter.EncodeRgba(size, size, rgba);
        if (full.Length <= maxBytes) return new EncodedIcon(full, false, 0);

        foreach (var colours in PaletteSteps)
        {
            var (indices, palette) = ColorQuantizer.Quantize(rgba, colours);
            var png = PngWriter.EncodeIndexed(size, size, indices, palette);
            if (png.Length <= maxBytes) return new EncodedIcon(png, true, palette.Length);
        }

        throw new InvalidOperationException($"Не удалось уложить картинку {size}×{size} в {maxBytes / 1000} КБ.");
    }
}
