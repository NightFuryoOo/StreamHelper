using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public sealed class ChatEmotes
{
    public const string ImageTag = "emote";
    public const string GiantTag = "giant-emote";
    private const double GiantScale = 4;

    private static readonly TimeSpan PreloadWait = TimeSpan.FromSeconds(3);

    private readonly ExtraEmoteCatalog _extra;
    private readonly ImageCache _images;
    private readonly TwitchEndpoints _endpoints;
    private readonly EmoteAnimator _animator = new();

    public ChatEmotes(ExtraEmoteCatalog extra, ImageCache images, TwitchEndpoints endpoints, EmoteLibrary? library = null)
    {
        _extra = extra;
        _images = images;
        _endpoints = endpoints;
        Library = library;
    }

    public EmoteLibrary? Library { get; }

    public EmoteFrames? CachedImage(string url) => _images.Get(url);

    public Task<EmoteFrames?> ImageAsync(string url) => _images.LoadAsync(url);

    public static double HeightFor(double fontSize) => Math.Round(fontSize * 1.8);

    public static double HeightFor(double fontSize, object? tag) => tag is GiantTag ? HeightFor(fontSize) * GiantScale : HeightFor(fontSize);

    public static bool IsEmote(object? tag) => tag is ImageTag or GiantTag;

    public Task EnsureCatalogAsync() => _extra.EnsureLoadedAsync();

    public void ResetChannel()
    {
        _extra.Reset();
        Library?.Reset();
    }

    public sealed record Piece(string Text, string? Url, bool Giant = false);

    private static bool IsTwitchEmote(ChatFragment fragment) => fragment.Type == "emote" && fragment.EmoteId.Length > 0;

    public IReadOnlyList<Piece> Pieces(ChatMessage message)
    {
        var fragments = message.Fragments is { Count: > 0 } ? message.Fragments : new[] { new ChatFragment("text", message.Text) };
        var pieces = new List<Piece>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            pieces.Add(new Piece(text.ToString(), null));
            text.Clear();
        }

        var giant = -1;
        if (message.GiantEmote)
        {
            for (var i = fragments.Count - 1; i >= 0 && giant < 0; i--)
            {
                if (IsTwitchEmote(fragments[i])) giant = i;
            }
        }
        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            if (IsTwitchEmote(fragment))
            {
                Flush();
                pieces.Add(new Piece(fragment.Text, _endpoints.EmoteUrl(fragment.EmoteId, fragment.Animated, i == giant), i == giant));
                continue;
            }
            foreach (var token in Split(fragment.Text))
            {
                if (token.Length > 0 && token[0] != ' ' && _extra.TryGet(token, out var emote))
                {
                    Flush();
                    pieces.Add(new Piece(token, emote.Url));
                }
                else
                {
                    text.Append(token);
                }
            }
        }
        Flush();
        return pieces;
    }

    public static IEnumerable<string> Split(string text)
    {
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && (text[i] == ' ') == (text[start] == ' ')) continue;
            yield return text[start..i];
            start = i;
        }
    }

    public Task PreloadAsync(ChatMessage message) => PreloadAsync(message, PreloadWait);

    public Task PreloadAsync(ChatMessage message, TimeSpan wait) => Task.WhenAny(CompleteAsync(message), Task.Delay(wait));

    public async Task CompleteAsync(ChatMessage message)
    {
        var loads = Urls(message).Select(_images.LoadAsync).ToList();
        if (_extra.Count == 0)
        {
            await _extra.EnsureLoadedAsync();
            loads.AddRange(Urls(message).Select(_images.LoadAsync));
        }
        await Task.WhenAll(loads);
    }

    public bool IsComplete(ChatMessage message) => (_extra.Count > 0 || !_extra.IsLoading) && Urls(message).All(url => _images.Get(url) != null);

    public int Shown(ChatMessage message) => Urls(message).Count(url => _images.Get(url) != null);

    private IEnumerable<string> Urls(ChatMessage message) => Pieces(message).Where(p => p.Url != null).Select(p => p.Url!).Distinct();

    public List<Inline> Inlines(ChatMessage message, double fontSize, EmoteAnimator? animator = null)
    {
        var inlines = new List<Inline>();
        foreach (var piece in Pieces(message))
        {
            var frames = piece.Url == null ? null : _images.Get(piece.Url);
            if (frames == null)
            {
                AddText(inlines, piece.Text);
                continue;
            }
            inlines.Add(Picture(piece.Text, frames, fontSize, piece.Giant ? GiantTag : ImageTag, animator ?? _animator));
        }
        return inlines;
    }

    private static readonly Brush LinkBrush = Solid(Color.FromRgb(0x6F, 0xB4, 0xFF));

    private static void AddText(List<Inline> inlines, string text)
    {
        foreach (var part in ChatLinks.Split(text))
        {
            if (part.Link is not { } uri)
            {
                inlines.Add(new Run(part.Text));
                continue;
            }
            inlines.Add(new Hyperlink(new Run(part.Text)) { NavigateUri = uri, Foreground = LinkBrush, ToolTip = uri.AbsoluteUri });
        }
    }

    private static InlineUIContainer Picture(string name, EmoteFrames frames, double fontSize, string tag, EmoteAnimator animator)
    {
        var image = new Image
        {
            Source = frames.Frames[0],
            Height = HeightFor(fontSize, tag),
            Stretch = Stretch.Uniform,
            Margin = new Thickness(1, 0, 1, 0),
            Tag = tag,
            ToolTip = name,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        animator.Add(image, frames);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    public void Stop() => _animator.Stop();
}