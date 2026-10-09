using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Tests;

internal static class TestImages
{
    public static byte[] Png(int width, int height, Color? color = null)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color ?? Color.OrangeRed);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static byte[] NoisyPng(int width, int height, int seed = 7, bool opaque = false)
    {
        var random = new Random(seed);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(opaque ? 255 : random.Next(1, 256), random.Next(256), random.Next(256), random.Next(256)));
            }
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    public static Bitmap Decode(byte[] png) => new(new MemoryStream(png));

    public static (int Width, int Height) SizeOf(string path)
    {
        using var image = Image.FromFile(path);
        return (image.Width, image.Height);
    }
}

public class ImageSizingTests
{
    [Theory]
    [InlineData(28)]
    [InlineData(56)]
    [InlineData(112)]
    public void Produces_exactly_the_requested_square_from_any_source_size(int size)
    {
        foreach (var source in new[] { (28, 28), (112, 112), (300, 300), (200, 100), (40, 90) })
        {
            using var stream = new MemoryStream(ImageSizing.ToSquarePng(TestImages.Png(source.Item1, source.Item2), size));
            using var image = Image.FromStream(stream);
            Assert.Equal((size, size), (image.Width, image.Height));
            Assert.Equal(ImageFormat.Png.Guid, image.RawFormat.Guid);
        }
    }

    [Fact]
    public void A_wide_picture_keeps_its_proportions_and_the_padding_stays_transparent()
    {
        using var stream = new MemoryStream(ImageSizing.ToSquarePng(TestImages.Png(200, 100, Color.Red), 56));
        using var image = new Bitmap(stream);

        Assert.Equal(0, image.GetPixel(28, 2).A);
        Assert.Equal(0, image.GetPixel(28, 53).A);
        Assert.Equal(255, image.GetPixel(28, 28).A);
        Assert.Equal(255, image.GetPixel(28, 28).R);
    }

    [Fact]
    public void Garbage_is_not_an_image()
    {
        Assert.ThrowsAny<Exception>(() => ImageSizing.ToSquarePng(new byte[] { 1, 2, 3 }, 28));
    }
}

public class PngWriterTests
{
    [Fact]
    public void An_rgba_image_decodes_to_exactly_the_same_pixels()
    {
        const int size = 16;
        var rgba = new byte[size * size * 4];
        var random = new Random(3);
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)random.Next(256);
            rgba[i + 1] = (byte)random.Next(256);
            rgba[i + 2] = (byte)random.Next(256);
            rgba[i + 3] = (byte)random.Next(1, 256);
        }

        using var image = TestImages.Decode(PngWriter.EncodeRgba(size, size, rgba));

        Assert.Equal((size, size), (image.Width, image.Height));
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                var p = image.GetPixel(x, y);
                Assert.Equal((rgba[o + 3], rgba[o], rgba[o + 1], rgba[o + 2]), (p.A, p.R, p.G, p.B));
            }
        }
    }

    [Fact]
    public void A_palette_image_keeps_its_colours_and_its_transparency()
    {
        var palette = new[] { new Rgba(0, 0, 0, 0), new Rgba(255, 0, 0, 255), new Rgba(0, 0, 255, 128) };
        var indices = new byte[] { 0, 1, 2, 1, 2, 0, 2, 2, 1 };

        using var image = TestImages.Decode(PngWriter.EncodeIndexed(3, 3, indices, palette));

        for (var i = 0; i < indices.Length; i++)
        {
            var p = image.GetPixel(i % 3, i / 3);
            var expected = palette[indices[i]];
            Assert.Equal(expected.A, p.A);
            if (expected.A != 0) Assert.Equal((expected.R, expected.G, expected.B), (p.R, p.G, p.B));
        }
    }

    [Fact]
    public void A_fully_opaque_palette_writes_no_transparency_chunk()
    {
        var png = PngWriter.EncodeIndexed(2, 2, new byte[] { 0, 1, 1, 0 }, new[] { new Rgba(1, 2, 3, 255), new Rgba(4, 5, 6, 255) });

        Assert.DoesNotContain("tRNS", System.Text.Encoding.ASCII.GetString(png));
        Assert.Contains("PLTE", System.Text.Encoding.ASCII.GetString(png));
    }

    [Fact]
    public void Wrong_buffer_sizes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => PngWriter.EncodeRgba(4, 4, new byte[10]));
        Assert.Throws<ArgumentException>(() => PngWriter.EncodeIndexed(4, 4, new byte[3], new[] { new Rgba(0, 0, 0, 255) }));
        Assert.Throws<ArgumentException>(() => PngWriter.EncodeIndexed(1, 1, new byte[1], Array.Empty<Rgba>()));
    }
}

public class IconEncoderTests
{
    [Fact]
    public void The_channels_come_out_in_the_right_order_and_alpha_is_not_premultiplied()
    {
        var source = TestImages.Png(40, 40, Color.FromArgb(128, 10, 200, 30));

        var rgba = ImageSizing.ToSquareRgba(source, 28);

        var centre = (14 * 28 + 14) * 4;
        Assert.InRange(rgba[centre], 7, 13);
        Assert.InRange(rgba[centre + 1], 197, 203);
        Assert.InRange(rgba[centre + 2], 27, 33);
        Assert.InRange(rgba[centre + 3], 125, 131);
    }

    [Fact]
    public void A_simple_picture_is_left_alone_and_costs_only_a_few_hundred_bytes()
    {
        var icon = IconEncoder.Encode(TestImages.Png(112, 112, Color.OrangeRed), 112, RewardIcons.MaxFileBytes);

        Assert.False(icon.ColoursReduced);
        Assert.True(icon.Png.Length < 1500, $"{icon.Png.Length} bytes");
        using var image = TestImages.Decode(icon.Png);
        Assert.Equal(Color.OrangeRed.ToArgb(), image.GetPixel(50, 50).ToArgb());
    }

    [Theory]
    [InlineData(28)]
    [InlineData(56)]
    [InlineData(112)]
    public void A_noisy_picture_is_brought_under_the_limit_at_the_exact_size(int size)
    {
        var icon = IconEncoder.Encode(TestImages.NoisyPng(300, 300), size, RewardIcons.MaxFileBytes);

        Assert.True(icon.Png.Length <= RewardIcons.MaxFileBytes, $"{icon.Png.Length} bytes");
        using var image = TestImages.Decode(icon.Png);
        Assert.Equal((size, size), (image.Width, image.Height));
    }

    [Fact]
    public void Reducing_colours_keeps_the_transparent_padding_transparent_and_the_picture_recognisable()
    {
        var icon = IconEncoder.Encode(TestImages.NoisyPng(400, 100, opaque: true), 112, 6000);

        Assert.True(icon.ColoursReduced);
        Assert.True(icon.Png.Length <= 6000, $"{icon.Png.Length} bytes");
        using var image = TestImages.Decode(icon.Png);
        Assert.Equal(0, image.GetPixel(56, 2).A);
        Assert.Equal(0, image.GetPixel(56, 109).A);
        Assert.InRange(image.GetPixel(56, 56).A, 250, 255);
    }

    [Fact]
    public void A_flat_colour_survives_colour_reduction_unchanged()
    {
        var icon = IconEncoder.Encode(TestImages.Png(112, 112, Color.SeaGreen), 112, 200);

        using var image = TestImages.Decode(icon.Png);
        Assert.Equal(Color.SeaGreen.ToArgb(), image.GetPixel(10, 10).ToArgb());
    }

    [Fact]
    public void A_limit_nothing_can_satisfy_is_an_error_not_an_oversized_file()
    {
        Assert.Throws<InvalidOperationException>(() => IconEncoder.Encode(TestImages.NoisyPng(112, 112), 112, 100));
    }
}

public class RewardIconTests
{
    private static RewardInfo R(string id, string title, string? u1 = null, string? u2 = null, string? u4 = null) =>
        new(id, title, 100, true)
        {
            ImageUrl1x = u1, ImageUrl2x = u2, ImageUrl4x = u4, ImageUrl = u4 ?? u2 ?? u1,
        };

    private static DownloadedImage Img(int size) => new(TestImages.Png(size, size), "image/png");

    [Fact]
    public void Parses_all_three_picture_sizes_and_treats_a_missing_or_null_one_as_the_default_icon()
    {
        var json = "{\"data\":[" +
                   "{\"id\":\"a\",\"title\":\"A\",\"image\":{\"url_1x\":\"https://x/1.png\",\"url_2x\":\"https://x/2.png\",\"url_4x\":\"https://x/4.png\"}}," +
                   "{\"id\":\"b\",\"title\":\"B\",\"image\":{\"url_1x\":\"https://x/b1.png\"}}," +
                   "{\"id\":\"c\",\"title\":\"C\",\"image\":null,\"default_image\":{\"url_1x\":\"https://x/d.png\"}}," +
                   "{\"id\":\"d\",\"title\":\"D\"}]}";

        var rewards = TwitchClient.ParseRewards(json).ToDictionary(r => r.Id);

        Assert.Equal("https://x/1.png", rewards["a"].ImageUrl1x);
        Assert.Equal("https://x/2.png", rewards["a"].ImageUrl2x);
        Assert.Equal("https://x/4.png", rewards["a"].ImageUrl4x);
        Assert.Equal("https://x/4.png", rewards["a"].ImageUrl);
        Assert.Equal("https://x/b1.png", rewards["b"].ImageUrl);
        Assert.Null(rewards["b"].ImageUrl4x);
        Assert.Null(rewards["c"].ImageUrl);
        Assert.Null(rewards["d"].ImageUrl);
    }

    [Fact]
    public async Task Every_reward_gets_its_own_folder_with_the_three_sizes_taken_from_the_three_pictures()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Заказать трек", "https://x/a1", "https://x/a2", "https://x/a4"));
        api.Images["https://x/a1"] = Img(28);
        api.Images["https://x/a2"] = Img(56);
        api.Images["https://x/a4"] = Img(112);

        var result = await RewardIcons.SaveAsync(api, canListManaged: true, dir.File("icons"), CancellationToken.None);

        var saved = Assert.Single(result.Saved);
        Assert.Equal("Заказать трек", saved.RewardTitle);
        Assert.Equal(dir.File(Path.Combine("icons", "Заказать трек")), saved.Folder);
        Assert.Equal(new[] { "112x112.png", "28x28.png", "56x56.png" }, Directory.GetFiles(saved.Folder).Select(Path.GetFileName).OrderBy(n => n).ToArray());
        Assert.Equal((28, 28), TestImages.SizeOf(Path.Combine(saved.Folder, "28x28.png")));
        Assert.Equal((56, 56), TestImages.SizeOf(Path.Combine(saved.Folder, "56x56.png")));
        Assert.Equal((112, 112), TestImages.SizeOf(Path.Combine(saved.Folder, "112x112.png")));
        Assert.Equal(0, result.Scaled);
        Assert.Equal(3, api.Downloads.Count);
    }

    [Fact]
    public async Task Every_saved_file_stays_under_25_kb_even_for_a_noisy_picture()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Шум", "https://x/a1", "https://x/a2", "https://x/a4"));
        api.Rewards.Add(R("b", "Простая", "https://x/b1", "https://x/b2", "https://x/b4"));
        api.Images["https://x/a1"] = new DownloadedImage(TestImages.NoisyPng(28, 28, 1), "image/png");
        api.Images["https://x/a2"] = new DownloadedImage(TestImages.NoisyPng(56, 56, 2), "image/png");
        api.Images["https://x/a4"] = new DownloadedImage(TestImages.NoisyPng(112, 112, 3), "image/png");
        api.Images["https://x/b1"] = Img(28);
        api.Images["https://x/b2"] = Img(56);
        api.Images["https://x/b4"] = Img(112);

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        Assert.Equal(2, result.Saved.Count);
        Assert.Empty(result.Failures);
        var sizes = result.Saved.SelectMany(s => Directory.GetFiles(s.Folder)).Select(p => new FileInfo(p).Length).ToArray();
        Assert.Equal(6, sizes.Length);
        Assert.All(sizes, s => Assert.True(s <= RewardIcons.MaxFileBytes, $"{s} bytes"));
        Assert.Equal(sizes.Max(), result.LargestBytes);
        Assert.True(result.ReducedFiles >= 1, "the noisy 112x112 file cannot fit without reducing colours");
    }

    [Fact]
    public async Task A_picture_that_fits_is_not_reduced_and_the_report_says_so()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Ровная", "https://x/a1", "https://x/a2", "https://x/a4"));
        api.Images["https://x/a1"] = Img(28);
        api.Images["https://x/a2"] = Img(56);
        api.Images["https://x/a4"] = Img(112);

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        Assert.Equal(0, result.ReducedFiles);
        Assert.InRange(result.LargestBytes, 1, RewardIcons.MaxFileBytes);
    }

    [Fact]
    public async Task A_size_twitch_does_not_offer_is_made_from_the_best_picture_and_exactly_sized()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Эмодзи", u1: "https://x/only1"));
        api.Images["https://x/only1"] = Img(28);

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        var saved = Assert.Single(result.Saved);
        Assert.Equal(1, result.Scaled);
        foreach (var size in new[] { 28, 56, 112 })
        {
            Assert.Equal((size, size), TestImages.SizeOf(Path.Combine(saved.Folder, $"{size}x{size}.png")));
        }
        Assert.Single(api.Downloads);
    }

    [Fact]
    public async Task A_picture_that_is_not_the_expected_size_is_still_saved_at_exactly_the_right_size()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Odd", "https://x/1", "https://x/2", "https://x/4"));
        api.Images["https://x/1"] = new DownloadedImage(TestImages.Png(50, 20), "image/png");
        api.Images["https://x/2"] = new DownloadedImage(TestImages.Png(300, 300), "image/png");
        api.Images["https://x/4"] = new DownloadedImage(TestImages.Png(112, 112), "image/png");

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        var folder = Assert.Single(result.Saved).Folder;
        Assert.Equal((28, 28), TestImages.SizeOf(Path.Combine(folder, "28x28.png")));
        Assert.Equal((56, 56), TestImages.SizeOf(Path.Combine(folder, "56x56.png")));
        Assert.Equal((112, 112), TestImages.SizeOf(Path.Combine(folder, "112x112.png")));
    }

    [Fact]
    public async Task Only_originals_with_a_picture_are_saved_and_the_default_icons_are_counted()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "С картинкой", "https://x/a1", null, "https://x/a4"));
        api.Rewards.Add(R("b", "Без картинки"));
        api.Rewards.Add(R("copy", "С картинкой\u200B", "https://x/c1", null, "https://x/c4"));
        api.Managed.Add("copy");
        api.Images["https://x/a1"] = Img(28);
        api.Images["https://x/a4"] = Img(112);

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        Assert.Equal("С картинкой", Assert.Single(result.Saved).RewardTitle);
        Assert.Equal(1, result.WithoutCustomImage);
        Assert.Empty(result.Failures);
        Assert.DoesNotContain(api.Downloads, u => u.Contains("/c"));
    }

    [Fact]
    public async Task Unsafe_and_repeated_titles_get_safe_unique_folder_names()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Что? Где: когда", "https://x/a"));
        api.Rewards.Add(R("b", "Same", "https://x/b"));
        api.Rewards.Add(R("c", "same", "https://x/c"));
        api.Rewards.Add(R("d", "\u200B", "https://x/d"));
        foreach (var name in new[] { "a", "b", "c", "d" }) api.Images[$"https://x/{name}"] = Img(28);

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        var names = result.Saved.Select(s => Path.GetFileName(s.Folder)).OrderBy(n => n).ToArray();
        Assert.Equal(4, names.Length);
        Assert.Equal(4, names.Select(n => n.ToLowerInvariant()).Distinct().Count());
        Assert.Contains("reward-d", names);
        Assert.All(names, n => Assert.True(n.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, n));
        Assert.All(result.Saved, s => Assert.Equal(3, Directory.GetFiles(s.Folder).Length));
    }

    [Fact]
    public async Task A_failed_or_broken_download_is_reported_and_does_not_stop_the_others()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "Раз", "https://x/boom"));
        api.Rewards.Add(R("b", "Два", "https://x/missing"));
        api.Rewards.Add(R("c", "Три", "https://x/c"));
        api.Rewards.Add(R("d", "Четыре", "https://x/junk"));
        api.Images["https://x/c"] = Img(28);
        api.Images["https://x/junk"] = new DownloadedImage(new byte[] { 1, 2, 3 }, "image/png");

        var result = await RewardIcons.SaveAsync(api, true, dir.File("icons"), CancellationToken.None);

        Assert.Equal("Три", Assert.Single(result.Saved).RewardTitle);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(result.Failures, f => f.StartsWith("Раз:") && f.Contains("network down"));
        Assert.Contains(result.Failures, f => f.StartsWith("Два:"));
        Assert.Contains(result.Failures, f => f.StartsWith("Четыре:"));
    }

    [Fact]
    public async Task Without_the_manage_right_every_reward_with_a_picture_is_a_candidate()
    {
        using var dir = new TempDir();
        var api = new FakeRewardApi();
        api.Rewards.Add(R("a", "A", "https://x/a"));
        api.Managed.Add("a");
        api.Images["https://x/a"] = Img(28);

        var result = await RewardIcons.SaveAsync(api, canListManaged: false, dir.File("icons"), CancellationToken.None);

        Assert.Single(result.Saved);
    }

    [Fact]
    public async Task The_download_sends_no_credentials_and_refuses_unsafe_addresses()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "PNGDATA");
        var settings = MakeFollower.Settings(dir);
        var client = new TwitchClient(settings, new HttpClient(), TwitchEndpoints.FromBase(da.BaseUrl));

        var image = await client.DownloadImageAsync(da.BaseUrl + "/img/a.png", CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal("PNGDATA"u8.ToArray(), image!.Data);
        var request = Assert.Single(da.Requests);
        Assert.Contains("auth=", request);
        Assert.DoesNotContain("Bearer", request);

        Assert.Null(await client.DownloadImageAsync("http://example.test/a.png", CancellationToken.None));
        Assert.Null(await client.DownloadImageAsync("file:///C:/secret.png", CancellationToken.None));
        Assert.Null(await client.DownloadImageAsync("not a url", CancellationToken.None));
        Assert.Single(da.Requests);
    }

    [Fact]
    public async Task A_missing_picture_gives_null()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (404, "");
        var client = new TwitchClient(MakeFollower.Settings(dir), new HttpClient(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Null(await client.DownloadImageAsync(da.BaseUrl + "/gone.png", CancellationToken.None));
    }

    [Fact]
    public void The_folder_chosen_for_the_icons_is_remembered_across_a_restart_and_is_empty_at_first()
    {
        using var dir = new TempDir();
        Assert.Equal("", new AppSettings().RewardIconsFolder);

        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.RewardIconsFolder = dir.File("my icons");
        store.Save();

        Assert.Equal(dir.File("my icons"), new SettingsStore(dir.File("settings.json")).Current.RewardIconsFolder);
    }
}
