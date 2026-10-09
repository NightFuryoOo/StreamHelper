using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class EmoteFrames
{
    public EmoteFrames(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysMs)
    {
        Frames = frames;
        DelaysMs = delaysMs;
        var ends = new int[frames.Count];
        var total = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            total += delaysMs[i];
            ends[i] = total;
        }
        _ends = ends;
        TotalMs = total;
    }

    private readonly int[] _ends;

    public IReadOnlyList<BitmapSource> Frames { get; }
    public IReadOnlyList<int> DelaysMs { get; }
    public int TotalMs { get; }
    public bool IsAnimated => Frames.Count > 1 && TotalMs > 0;

    public int FrameAt(long ms)
    {
        if (!IsAnimated) return 0;
        var t = (int)(ms % TotalMs);
        var index = Array.BinarySearch(_ends, t);
        return index >= 0 ? Math.Min(index + 1, Frames.Count - 1) : ~index;
    }
}

public static class EmoteDecoder
{
    private const int MinDelayMs = 20;

    public static EmoteFrames? Decode(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 8) return null;
            return bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' ? DecodeGif(bytes) : DecodeStill(bytes);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or IOException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            Log.Write("Emote picture could not be read: " + ex.Message);
            return null;
        }
    }

    private static EmoteFrames? DecodeStill(byte[] bytes)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        frame.Freeze();
        return new EmoteFrames(new BitmapSource[] { frame }, new[] { 1000 });
    }

    private static EmoteFrames? DecodeGif(byte[] bytes)
    {
        var decoder = new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames;
        if (source.Count == 0) return null;

        ImageMetadata? container = null;
        try
        {
            container = decoder.Metadata;
        }
        catch (NotSupportedException)
        {
        }
        var width = Query(container, "/logscrdesc/Width", 0);
        var height = Query(container, "/logscrdesc/Height", 0);
        if (width <= 0 || height <= 0)
        {
            width = source.Max(f => Query(f.Metadata, "/imgdesc/Left", 0) + f.PixelWidth);
            height = source.Max(f => Query(f.Metadata, "/imgdesc/Top", 0) + f.PixelHeight);
        }

        var canvas = new byte[width * height * 4];
        byte[]? backup = null;
        var previousDisposal = 0;
        var previousRect = (Left: 0, Top: 0, Width: 0, Height: 0);
        var frames = new List<BitmapSource>(source.Count);
        var delays = new List<int>(source.Count);

        foreach (var frame in source)
        {
            if (previousDisposal == 2) Clear(canvas, width, height, previousRect);
            else if (previousDisposal == 3 && backup != null) canvas = (byte[])backup.Clone();

            var meta = frame.Metadata;
            var left = Query(meta, "/imgdesc/Left", 0);
            var top = Query(meta, "/imgdesc/Top", 0);
            var disposal = Query(meta, "/grctlext/Disposal", 0);
            var delay = Query(meta, "/grctlext/Delay", 10) * 10;
            if (delay <= 10) delay = 100;
            delay = Math.Max(delay, MinDelayMs);

            backup = disposal == 3 ? (byte[])canvas.Clone() : null;

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var w = converted.PixelWidth;
            var h = converted.PixelHeight;
            var pixels = new byte[w * h * 4];
            converted.CopyPixels(pixels, w * 4, 0);
            for (var y = 0; y < h; y++)
            {
                var cy = top + y;
                if (cy < 0 || cy >= height) continue;
                for (var x = 0; x < w; x++)
                {
                    var cx = left + x;
                    if (cx < 0 || cx >= width) continue;
                    var s = (y * w + x) * 4;
                    if (pixels[s + 3] == 0) continue;
                    var d = (cy * width + cx) * 4;
                    canvas[d] = pixels[s];
                    canvas[d + 1] = pixels[s + 1];
                    canvas[d + 2] = pixels[s + 2];
                    canvas[d + 3] = pixels[s + 3];
                }
            }

            var shot = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, (byte[])canvas.Clone(), width * 4);
            shot.Freeze();
            frames.Add(shot);
            delays.Add(delay);

            previousDisposal = disposal;
            previousRect = (left, top, w, h);
        }
        return new EmoteFrames(frames, delays);
    }

    private static void Clear(byte[] canvas, int width, int height, (int Left, int Top, int Width, int Height) rect)
    {
        for (var y = Math.Max(0, rect.Top); y < Math.Min(height, rect.Top + rect.Height); y++)
        {
            var from = (y * width + Math.Max(0, rect.Left)) * 4;
            var to = (y * width + Math.Min(width, rect.Left + rect.Width)) * 4;
            if (to > from) Array.Clear(canvas, from, to - from);
        }
    }

    private static int Query(ImageMetadata? metadata, string query, int fallback)
    {
        try
        {
            if (metadata is BitmapMetadata bm && bm.ContainsQuery(query)) return Convert.ToInt32(bm.GetQuery(query));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
        }
        return fallback;
    }
}

public sealed class EmoteImageCache
{
    private const int MaxBytes = 3 * 1024 * 1024;
    private const int KeepAtMost = 400;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public EmoteImageCache(HttpClient http) => _http = http;

    private sealed record Entry(Task<EmoteFrames?> Task, DateTime StartedUtc);

    public EmoteFrames? Get(string url) =>
        _entries.TryGetValue(url, out var entry) && entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;

    public Task<EmoteFrames?> LoadAsync(string url)
    {
        if (_entries.TryGetValue(url, out var entry))
        {
            var failed = entry.Task.IsCompleted && (entry.Task.IsFaulted || entry.Task.Result == null);
            if (!failed || DateTime.UtcNow - entry.StartedUtc < RetryAfter) return entry.Task;
        }
        if (_entries.Count >= KeepAtMost) _entries.Clear();
        var fresh = new Entry(FetchAsync(url), DateTime.UtcNow);
        _entries[url] = fresh;
        return fresh.Task;
    }

    private async Task<EmoteFrames?> FetchAsync(string url)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > MaxBytes) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (bytes.Length > MaxBytes) return null;
            return await Task.Run(() => EmoteDecoder.Decode(bytes)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            Log.Write("Emote picture failed: " + ex.Message);
            return null;
        }
    }
}

public sealed class EmoteAnimator
{
    private readonly int _keepAtMost;

    private sealed class Item
    {
        public Item(WeakReference<Image> image, EmoteFrames frames)
        {
            Image = image;
            Frames = frames;
        }

        public WeakReference<Image> Image { get; }
        public EmoteFrames Frames { get; }
        public int Shown { get; set; }
    }

    private readonly List<Item> _items = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public EmoteAnimator(int keepAtMost = 600)
    {
        _keepAtMost = keepAtMost;
        _timer.Tick += (_, _) => Step();
    }

    public void Add(Image image, EmoteFrames frames)
    {
        if (!frames.IsAnimated) return;
        _items.Add(new Item(new WeakReference<Image>(image), frames));
        if (_items.Count > _keepAtMost) _items.RemoveRange(0, _items.Count - _keepAtMost);
        if (!_timer.IsEnabled) _timer.Start();
    }

    public void Stop() => _timer.Stop();

    public void Clear() => _items.Clear();

    private void Step()
    {
        var now = _clock.ElapsedMilliseconds;
        var anyAlive = false;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (!item.Image.TryGetTarget(out var image))
            {
                _items.RemoveAt(i);
                continue;
            }
            anyAlive = true;
            var index = item.Frames.FrameAt(now);
            if (index == item.Shown) continue;
            item.Shown = index;
            image.Source = item.Frames.Frames[index];
        }
        if (!anyAlive) _timer.Stop();
    }
}