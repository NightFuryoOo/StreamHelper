using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ChatSelectionTests
{
    private static TextBlock Line()
    {
        var picture = BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        text.Inlines.Add(new InlineUIContainer(new Image { Source = picture, Width = 16, Height = 16 }));
        text.Inlines.Add(new Run("Bob") { FontWeight = FontWeights.Bold });
        text.Inlines.Add(new Run(": "));
        text.Inlines.Add(new Run("привет "));
        text.Inlines.Add(new InlineUIContainer(new Image { Source = picture, Height = 25, Tag = ChatEmotes.ImageTag, ToolTip = "furaNYAM" }));
        text.Inlines.Add(new Run(" смотри "));
        text.Inlines.Add(new Hyperlink(new Run("https://example.com/x")));
        text.Inlines.Add(new Run(" пока"));
        return text;
    }

    [Fact]
    public void The_copied_text_keeps_emotes_by_name_and_skips_badges()
    {
        Sta.Run(() =>
        {
            var text = Line();

            Assert.Equal("Bob: привет furaNYAM смотри https://example.com/x пока", ChatSelection.TextBetween(text.ContentStart, text.ContentEnd));
        });
    }

    [Fact]
    public void A_part_of_a_line_gives_only_that_part()
    {
        Sta.Run(() =>
        {
            var text = Line();
            var all = ChatSelection.TextBetween(text.ContentStart, text.ContentEnd);
            var start = text.ContentStart;
            while (!ChatSelection.TextBetween(start, text.ContentEnd).StartsWith("ивет", StringComparison.Ordinal)) start = start.GetNextInsertionPosition(LogicalDirection.Forward)!;
            var end = start;
            while (ChatSelection.TextBetween(start, end) != "ивет furaNYAM смо") end = end.GetNextInsertionPosition(LogicalDirection.Forward)!;

            Assert.Equal("ивет furaNYAM смо", ChatSelection.TextBetween(start, end));
            Assert.Equal("", ChatSelection.TextBetween(end, end));
            Assert.EndsWith(" пока", all);
        });
    }

    [Fact]
    public void A_wrapped_message_is_highlighted_line_by_line_and_a_short_one_with_one_mark()
    {
        Sta.Run(() =>
        {
            var text = new TextBlock { Text = "раз два три четыре пять шесть семь восемь девять десять", TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            text.Measure(new Size(120, double.PositiveInfinity));
            text.Arrange(new Rect(text.DesiredSize));

            var rects = ChatSelection.Rects(text.ContentStart, text.ContentEnd);

            Assert.True(rects.Count >= 3, $"{rects.Count} marks");
            Assert.All(rects, r => Assert.True(r.Width > 5 && r.Height > 5));
            Assert.True(rects.Zip(rects.Skip(1)).All(pair => pair.Second.Top >= pair.First.Bottom - 1));

            var word = new TextBlock { Text = "коротко", FontSize = 14 };
            word.Measure(new Size(300, double.PositiveInfinity));
            word.Arrange(new Rect(word.DesiredSize));
            var one = Assert.Single(ChatSelection.Rects(word.ContentStart, word.ContentEnd));
            Assert.InRange(one.Width, word.ActualWidth - 3, word.ActualWidth + 1);
        });
    }
}
