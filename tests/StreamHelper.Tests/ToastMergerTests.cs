using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ToastMergerTests
{
    private sealed class Sink : IToastSink
    {
        public List<string> Shown { get; } = new();
        public List<string> Updated { get; } = new();
        public bool Alive { get; set; } = true;

        public object Show(ToastText text)
        {
            Shown.Add(text.Title + " | " + text.Line);
            return Shown.Count;
        }

        public bool Update(object toast, ToastText text)
        {
            if (!Alive) return false;
            Updated.Add(toast + ": " + text.Title + " | " + text.Line);
            return true;
        }
    }

    private static ToastText Subs(IReadOnlyList<string> names) =>
        new(names.Count == 1 ? "Подарочная подписка" : $"Подарочных подписок: {names.Count}", names[^1], null);

    private static ToastText Follows(IReadOnlyList<Uri> who) => new($"Фолловеров: {who.Count}", who[^1].Host, null);

    [Fact]
    public void A_burst_of_the_same_kind_grows_one_notification_instead_of_opening_new_ones()
    {
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var sink = new Sink();
        var merger = new ToastMerger(sink, () => now);

        merger.Show(new[] { "Alice" }, Subs);
        now = now.AddSeconds(1);
        merger.Show(new[] { "Bob" }, Subs);
        now = now.AddSeconds(2.5);
        merger.Show(new[] { "Carol", "Dave" }, Subs);

        Assert.Equal(new[] { "Подарочная подписка | Alice" }, sink.Shown);
        Assert.Equal(new[] { "1: Подарочных подписок: 2 | Bob", "1: Подарочных подписок: 4 | Dave" }, sink.Updated);
    }

    [Fact]
    public void After_a_pause_or_once_the_notification_is_gone_a_new_one_opens()
    {
        var now = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
        var sink = new Sink();
        var merger = new ToastMerger(sink, () => now);

        merger.Show(new[] { "Alice" }, Subs);
        now = now.AddSeconds(4);
        merger.Show(new[] { "Bob" }, Subs);
        now = now.AddSeconds(1);
        sink.Alive = false;
        merger.Show(new[] { "Carol" }, Subs);

        Assert.Equal(new[] { "Подарочная подписка | Alice", "Подарочная подписка | Bob", "Подарочная подписка | Carol" }, sink.Shown);
        Assert.Empty(sink.Updated);
    }

    [Fact]
    public void Different_kinds_never_share_a_notification()
    {
        var sink = new Sink();
        var merger = new ToastMerger(sink);

        merger.Show(new[] { "Alice" }, Subs);
        merger.Show(new[] { new Uri("https://bob.example") }, Follows);
        merger.Show(Array.Empty<string>(), Subs);

        Assert.Equal(new[] { "Подарочная подписка | Alice", "Фолловеров: 1 | bob.example" }, sink.Shown);
        Assert.Empty(sink.Updated);
    }
}

public class EmoteImageTests
{
    private static BitmapSource Pixel(byte shade)
    {
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat(shade, 64).ToArray(), 16);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public void A_new_frame_only_repaints_and_never_asks_for_a_new_layout()
    {
        Sta.Run(() =>
        {
            var first = Pixel(10);
            var second = Pixel(200);
            var image = new EmoteImage { Source = first, Height = 20, Stretch = Stretch.Uniform };
            image.Measure(new Size(100, 100));
            image.Arrange(new Rect(0, 0, 20, 20));
            Assert.True(image.IsMeasureValid && image.IsArrangeValid);

            image.ShowFrame(second);

            Assert.Same(second, image.Frame);
            Assert.Same(first, image.Source);
            Assert.True(image.IsMeasureValid && image.IsArrangeValid);
        });
    }
}
