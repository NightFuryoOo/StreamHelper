using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed record ClipOutcome(bool Success, string Title, string Line, string? Picture = null, string? Caption = null, StreamClip? Clip = null);

public sealed class ClipMaker
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GiveUp = TimeSpan.FromSeconds(60);

    public const string Busy = "Клип уже создаётся, подожди.";
    public const string Started = "Клип создаётся…";

    public const string SavedOnChannel = "Клип сохранён на канале";

    private readonly IClipMakerApi _api;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<bool> _linkToChat;
    private readonly Func<DateTime> _now;
    private int _busy;

    public ClipMaker(IClipMakerApi api, Func<TimeSpan, Task>? delay = null, Func<bool>? linkToChat = null, Func<DateTime>? now = null)
    {
        _api = api;
        _delay = delay ?? (wait => Task.Delay(wait));
        _linkToChat = linkToChat ?? (() => true);
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public static string ChatText(string url) => "Клип: " + url;

    public async Task<ClipOutcome> MakeAsync(Action<ClipOutcome>? started = null)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new ClipOutcome(false, "Клип", Busy);
        try
        {
            var askedUtc = _now();
            var start = await _api.CreateClipAsync(CancellationToken.None);
            if (!start.Success)
            {
                Log.Write("Making a clip failed: " + start.Message);
                return new ClipOutcome(false, "Клип не сделан", start.Message);
            }
            started?.Invoke(new ClipOutcome(true, Started, "Twitch собирает клип из последних секунд эфира"));

            LinkPreview? clip = null;
            var waited = TimeSpan.Zero;
            var wait = FirstCheck;
            while (clip == null && waited < GiveUp)
            {
                await _delay(wait);
                waited += wait;
                wait = Every;
                try
                {
                    clip = await _api.GetClipAsync(start.Id, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException)
                {
                    Log.Write("Checking the new clip failed: " + ex.Message);
                }
            }
            var toChat = _linkToChat();
            if (clip == null)
            {
                Log.Write($"Clip {start.Id} did not show up within a minute.");
                return new ClipOutcome(false, "Клип не появился", toChat ? "Twitch не показал клип за минуту, ссылка в чат не отправлена." : "Twitch не показал клип за минуту.");
            }

            var url = clip.Url.Length > 0 ? clip.Url : "https://clips.twitch.tv/" + start.Id;
            Log.Write("Clip made: " + url);
            var made = new StreamClip
            {
                Id = start.Id,
                Url = url,
                Title = clip.Title.Trim(),
                CreatorName = clip.Credit,
                CreatedUtc = askedUtc,
                Seconds = clip.Duration?.TotalSeconds ?? 0,
                ThumbnailUrl = clip.ThumbnailUrl,
            };
            var picture = clip.ThumbnailUrl.Length > 0 ? clip.ThumbnailUrl : null;
            if (!toChat) return new ClipOutcome(true, "Клип готов", SavedOnChannel, picture, clip.Title, made);
            var sent = await _api.SendOwnChatMessageAsync(ChatText(url), CancellationToken.None);
            var line = sent.Success ? "Ссылка отправлена в чат" : "В чат не отправлено: " + (sent.Message.Length > 0 ? sent.Message : "Twitch не принял сообщение.");
            return new ClipOutcome(true, "Клип готов", line, picture, clip.Title, made);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }
}
