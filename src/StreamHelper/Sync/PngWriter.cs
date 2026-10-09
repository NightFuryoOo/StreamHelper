using System;
using System.IO;
using System.IO.Compression;

namespace StreamHelper.Sync;

public static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeRgba(int width, int height, byte[] rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("Unexpected pixel buffer size.", nameof(rgba));
        return Encode(width, height, colorType: 6, bytesPerPixel: 4, rgba, palette: null);
    }

    public static byte[] EncodeIndexed(int width, int height, byte[] indices, Rgba[] palette)
    {
        if (indices.Length != width * height) throw new ArgumentException("Unexpected index buffer size.", nameof(indices));
        if (palette.Length is < 1 or > 256) throw new ArgumentException("A palette holds 1 to 256 colours.", nameof(palette));
        return Encode(width, height, colorType: 3, bytesPerPixel: 1, indices, palette);
    }

    private static byte[] Encode(int width, int height, byte colorType, int bytesPerPixel, byte[] pixels, Rgba[]? palette)
    {
        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8;
        header[9] = colorType;
        WriteChunk(output, "IHDR", header);

        if (palette != null)
        {
            var plte = new byte[palette.Length * 3];
            for (var i = 0; i < palette.Length; i++)
            {
                plte[i * 3] = palette[i].R;
                plte[i * 3 + 1] = palette[i].G;
                plte[i * 3 + 2] = palette[i].B;
            }
            WriteChunk(output, "PLTE", plte);

            var lastTranslucent = -1;
            for (var i = 0; i < palette.Length; i++)
            {
                if (palette[i].A != 255) lastTranslucent = i;
            }
            if (lastTranslucent >= 0)
            {
                var trns = new byte[lastTranslucent + 1];
                for (var i = 0; i <= lastTranslucent; i++) trns[i] = palette[i].A;
                WriteChunk(output, "tRNS", trns);
            }
        }

        WriteChunk(output, "IDAT", Compress(Filter(width, height, bytesPerPixel, pixels)));
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static byte[] Filter(int width, int height, int bytesPerPixel, byte[] pixels)
    {
        var stride = width * bytesPerPixel;
        var result = new byte[(stride + 1) * height];
        var zero = new byte[stride];
        var candidates = new byte[5][];
        for (var i = 0; i < candidates.Length; i++) candidates[i] = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var row = new ReadOnlySpan<byte>(pixels, y * stride, stride);
            var prior = y == 0 ? zero : new ReadOnlySpan<byte>(pixels, (y - 1) * stride, stride).ToArray();

            for (var x = 0; x < stride; x++)
            {
                var left = x >= bytesPerPixel ? row[x - bytesPerPixel] : 0;
                var up = prior[x];
                var upLeft = x >= bytesPerPixel ? prior[x - bytesPerPixel] : 0;
                candidates[0][x] = row[x];
                candidates[1][x] = (byte)(row[x] - left);
                candidates[2][x] = (byte)(row[x] - up);
                candidates[3][x] = (byte)(row[x] - ((left + up) >> 1));
                candidates[4][x] = (byte)(row[x] - Paeth(left, up, upLeft));
            }

            var best = 0;
            var bestScore = long.MaxValue;
            for (var f = 0; f < candidates.Length; f++)
            {
                long score = 0;
                foreach (var b in candidates[f]) score += Math.Abs((int)(sbyte)b);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = f;
                }
            }

            var offset = y * (stride + 1);
            result[offset] = (byte)best;
            Buffer.BlockCopy(candidates[best], 0, result, offset + 1, stride);
        }
        return result;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return buffer.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        output.Write(length);

        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) typed[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typed, 4, data.Length);
        output.Write(typed);

        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(typed)));
        output.Write(crc);
    }

    private static void WriteInt(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

public readonly record struct Rgba(byte R, byte G, byte B, byte A);
