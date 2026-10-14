using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed record HelpKey(string Name, string Key, bool IsSet);

public sealed record HelpPicture(ImageSource Source, string Name);

public sealed class HelpCard
{
    public string Title { get; init; } = "";
    public string Section { get; init; } = "";
    public bool ShowSection { get; init; }
    public IReadOnlyList<string> Paragraphs { get; init; } = Array.Empty<string>();
    public string Where { get; init; } = "";
    public bool HasWhere => Where.Length > 0;
    public string Limit { get; init; } = "";
    public bool HasLimit => Limit.Length > 0;
    public IReadOnlyList<HelpKey> Keys { get; init; } = Array.Empty<HelpKey>();
    public bool HasKeys => Keys.Count > 0;
    public string Page { get; init; } = "";
    public bool HasPage => Page.Length > 0;
    public IReadOnlyList<HelpPicture> Pictures { get; init; } = Array.Empty<HelpPicture>();
    public bool HasPictures => Pictures.Count > 0;

    public static Uri PictureUri(string image) => new("pack://application:,,,/StreamHelper;component/Assets/Help/" + image, UriKind.Absolute);

    public static HelpCard From(HelpArticle article, AppSettings settings, bool showSection) => new()
    {
        Title = article.Title,
        Section = article.Section,
        ShowSection = showSection,
        Paragraphs = article.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Where = article.Where,
        Limit = article.Limit,
        Keys = article.Keys.Select(action =>
        {
            var binding = settings.GetHotkey(action);
            return new HelpKey(HotkeyActions.Title(action), HotkeyService.Describe(binding), binding.IsSet);
        }).ToList(),
        Page = article.Page,
        Pictures = article.Images.Select(LoadPicture).OfType<ImageSource>().Select(picture => new HelpPicture(picture, article.Title)).ToList(),
    };

    private static ImageSource? LoadPicture(string image)
    {
        if (image.Length == 0) return null;
        try
        {
            if (Application.GetResourceStream(PictureUri(image)) is not { } resource) return null;
            using var stream = resource.Stream;
            var picture = new BitmapImage();
            picture.BeginInit();
            picture.StreamSource = stream;
            picture.CacheOption = BitmapCacheOption.OnLoad;
            picture.EndInit();
            picture.Freeze();
            return picture;
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or NotSupportedException)
        {
            Log.Write($"Help picture {image} failed: {ex.Message}");
            return null;
        }
    }
}
