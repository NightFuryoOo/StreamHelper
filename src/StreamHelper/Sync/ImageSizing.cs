using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace StreamHelper.Sync;

public static class ImageSizing
{
    public static byte[] ToSquarePng(byte[] data, int size)
    {
        using var square = RenderSquare(data, size);
        using var output = new MemoryStream();
        square.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    public static byte[] ToSquareRgba(byte[] data, int size)
    {
        using var square = RenderSquare(data, size);
        var rect = new Rectangle(0, 0, size, size);
        var bits = square.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var raw = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, raw, y * size * 4, size * 4);
            }
            for (var i = 0; i < raw.Length; i += 4) (raw[i], raw[i + 2]) = (raw[i + 2], raw[i]);
            return raw;
        }
        finally
        {
            square.UnlockBits(bits);
        }
    }

    private static Bitmap RenderSquare(byte[] data, int size)
    {
        using var input = new MemoryStream(data);
        using var source = Image.FromStream(input);
        var square = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(square);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;

        var scale = Math.Min((double)size / source.Width, (double)size / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        g.DrawImage(source, (size - width) / 2, (size - height) / 2, width, height);
        return square;
    }
}
