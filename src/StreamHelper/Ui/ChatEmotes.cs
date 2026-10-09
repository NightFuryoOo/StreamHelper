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

namespace StreamHelper.Ui;

public sealed class ChatEmotes
{
    public const string ImageTag = "emote";

    private static readonly TimeSpan PreloadWait = TimeSpan.FromSeconds(3);

    private readonly SevenTvCatalog _seven;
    private readonly EmoteImageCache _images;
    private readonly TwitchEndpoints _endpoints;
    private readonly EmoteAnimator _animator = new();

    public ChatEmotes(SevenTvCatalog seven, EmoteImageCache images, TwitchEndpoints endpoints)
    {
        _seven = seven;
        _images = images;
        _endpoints = endpoints;
    }

    public static double HeightFor(double fontSize) => Math.Round(fontSize * 1.8);

    public Task EnsureCatalogAsync() => _seven.EnsureLoadedAsync();

    public bool CatalogLoading => _seven.IsLoading;

    public sealed record Piece(string Text, string? Url);

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

        foreach (var fragment in fragments)
        {
            if (fragment.Type == "emote" && fragment.EmoteId.Length > 0)
            {
                Flush();
                pieces.Add(new Piece(fragment.Text, _endpoints.EmoteUrl(fragment.EmoteId, fragment.Animated)));
                continue;
            }
            foreach (var token in Split(fragment.Text))
            {
                if (token.Length > 0 && token[0] != ' ' && _seven.TryGet(token, out var emote))
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

    public async Task PreloadAsync(ChatMessage message)
    {
        var work = PreloadCoreAsync(message);
        await Task.WhenAny(work, Task.Delay(PreloadWait));
    }

    private async Task PreloadCoreAsync(ChatMessage message)
    {
        if (_seven.Count == 0) await _seven.EnsureLoadedAsync();
        var urls = Pieces(message).Where(p => p.Url != null).Select(p => p.Url!).Distinct().ToList();
        if (urls.Count > 0) await Task.WhenAll(urls.Select(u => _images.LoadAsync(u)));
    }

    public IReadOnlyList<string> Missing(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(Pieces).Where(p => p.Url != null && _images.Get(p.Url) == null).Select(p => p.Url!).Distinct().ToList();

    public async Task PreloadAsync(IEnumerable<ChatMessage> messages, TimeSpan wait)
    {
        var list = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        if (_seven.Count == 0) await Task.WhenAny(_seven.EnsureLoadedAsync(), Task.Delay(wait));
        var urls = Missing(list);
        if (urls.Count > 0) await Task.WhenAny(Task.WhenAll(urls.Select(u => _images.LoadAsync(u))), Task.Delay(wait));
    }

    public List<Inline> Inlines(ChatMessage message, double fontSize, EmoteAnimator? animator = null)
    {
        var inlines = new List<Inline>();
        foreach (var piece in Pieces(message))
        {
            var frames = piece.Url == null ? null : _images.Get(piece.Url);
            if (frames == null)
            {
                inlines.Add(new Run(piece.Text));
                continue;
            }
            inlines.Add(Picture(piece.Text, frames, fontSize, animator ?? _animator));
        }
        return inlines;
    }

    private static InlineUIContainer Picture(string name, EmoteFrames frames, double fontSize, EmoteAnimator animator)
    {
        var image = new Image
        {
            Source = frames.Frames[0],
            Height = HeightFor(fontSize),
            Stretch = Stretch.Uniform,
            Margin = new Thickness(1, 0, 1, 0),
            Tag = ImageTag,
            ToolTip = name,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        animator.Add(image, frames);
        return new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center };
    }

    public void Stop() => _animator.Stop();
}