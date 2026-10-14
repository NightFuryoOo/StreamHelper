using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class LinkPreviewCatalog
{
    private const int KeepAtMost = 300;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(15);

    private readonly IClipApi _clips;
    private readonly IVideoApi _videos;
    private readonly ImageCache _images;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public LinkPreviewCatalog(IClipApi clips, IVideoApi videos, ImageCache images)
    {
        _clips = clips;
        _videos = videos;
        _images = images;
    }

    private sealed class Entry
    {
        public Task<LinkPreview?> Task { get; set; } = System.Threading.Tasks.Task.FromResult<LinkPreview?>(null);
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public bool Failed { get; set; }
    }

    public static PreviewTarget? TargetOf(ChatMessage message) => TargetIn(message.Text);

    public static PreviewTarget? TargetIn(string text)
    {
        foreach (var part in ChatLinks.Split(text))
        {
            if (part.Link is { } uri && PreviewLinks.Find(uri) is { } target) return target;
        }
        return null;
    }

    public async Task AttachAsync(Redemption redemption)
    {
        if (redemption.HasVideoTitle || TargetIn(redemption.UserInput) is not { } target) return;
        var clip = target.Kind == PreviewKind.TwitchClip;
        if (!clip && !redemption.HasVideo) redemption.AttachVideo(target.Url, "", YouTubeImages.Thumbnail(target.Id), clip: false);
        if (await LoadAsync(target) is { } preview) redemption.AttachVideo(target.Url, preview.Title, preview.ThumbnailUrl, clip);
    }

    public LinkPreview? Get(PreviewTarget target) =>
        _entries.TryGetValue(KeyOf(target), out var entry) && entry.Task.IsCompletedSuccessfully ? entry.Task.Result : null;

    public bool IsSettled(PreviewTarget target) =>
        _entries.TryGetValue(KeyOf(target), out var entry) && entry.Task.IsCompleted && !(entry.Failed && Due(entry));

    public EmoteFrames? Thumbnail(LinkPreview preview) => preview.ThumbnailUrl.Length == 0 ? null : _images.Get(preview.ThumbnailUrl);

    public Task<LinkPreview?> LoadAsync(PreviewTarget target)
    {
        var key = KeyOf(target);
        if (_entries.TryGetValue(key, out var entry) && !(entry.Task.IsCompleted && entry.Failed && Due(entry))) return entry.Task;
        if (_entries.Count >= KeepAtMost) Trim();
        var fresh = new Entry();
        fresh.Task = FetchAsync(target, fresh);
        _entries[key] = fresh;
        return fresh.Task;
    }

    private async Task<LinkPreview?> FetchAsync(PreviewTarget target, Entry entry)
    {
        try
        {
            using var cts = new CancellationTokenSource(AskLimit);
            var preview = target.Kind == PreviewKind.TwitchClip
                ? await _clips.GetClipAsync(target.Id, cts.Token)
                : await _videos.GetVideoAsync(target.Id, cts.Token);
            if (preview != null && preview.ThumbnailUrl.Length > 0) await _images.LoadAsync(preview.ThumbnailUrl);
            return preview;
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException or JsonException)
        {
            entry.Failed = true;
            Log.Write($"Link preview failed ({target.Kind} {target.Id}): {ex.Message}");
            return null;
        }
    }

    private static string KeyOf(PreviewTarget target) => (target.Kind == PreviewKind.TwitchClip ? "clip:" : "video:") + target.Id;

    private static bool Due(Entry entry) => DateTime.UtcNow - entry.StartedUtc >= RetryAfter;

    private void Trim()
    {
        var oldest = _entries.Where(pair => pair.Value.Task.IsCompleted).OrderBy(pair => pair.Value.StartedUtc).Take(_entries.Count - KeepAtMost * 3 / 4).ToList();
        foreach (var pair in oldest) _entries.TryRemove(pair.Key, out _);
    }
}
