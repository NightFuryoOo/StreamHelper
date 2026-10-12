using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Documents;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class EmoteInputTests
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "Kappa", "PogChamp", "furaSHTUKNOST" };

    private static bool IsEmote(string word) => Known.Contains(word);

    private static string Starts(IReadOnlyList<EmoteSpan> spans) => string.Join(",", spans.Select(s => $"{s.Start}+{s.Length}"));

    [Fact]
    public void The_code_being_typed_stays_text_until_a_space_follows_it()
    {
        Assert.Equal("", Starts(EmoteInput.Images("hi Kappa", new HashSet<int>(), 8, IsEmote)));
        Assert.Equal("3+5", Starts(EmoteInput.Images("hi Kappa ", new HashSet<int>(), 9, IsEmote)));
    }

    [Fact]
    public void A_code_the_caret_has_left_or_sits_before_becomes_a_picture()
    {
        Assert.Equal("3+5", Starts(EmoteInput.Images("hi Kappa", new HashSet<int>(), 2, IsEmote)));
        Assert.Equal("3+5", Starts(EmoteInput.Images("hi Kappa", new HashSet<int>(), 3, IsEmote)));
        Assert.Equal("", Starts(EmoteInput.Images("hi Kappa", new HashSet<int>(), 5, IsEmote)));
    }

    [Fact]
    public void A_picture_already_shown_stays_a_picture_even_with_the_caret_right_after_it()
    {
        Assert.Equal("0+5", Starts(EmoteInput.Images("Kappa", new HashSet<int> { 0 }, 5, IsEmote)));
    }

    [Fact]
    public void A_picture_glued_to_other_letters_turns_back_into_text_because_that_is_what_gets_sent()
    {
        Assert.Equal("", Starts(EmoteInput.Images("Kappax", new HashSet<int> { 0 }, 6, IsEmote)));
        Assert.Equal("", Starts(EmoteInput.Images("KappaKappa", new HashSet<int> { 0, 5 }, 10, IsEmote)));
    }

    [Fact]
    public void Inserted_text_turns_every_whole_code_into_a_picture_and_codes_are_case_sensitive()
    {
        Assert.Equal("0+13,14+13,28+5", Starts(EmoteInput.Images("furaSHTUKNOST furaSHTUKNOST Kappa kappa Kap", new HashSet<int>(), -1, IsEmote)));
    }

    [Fact]
    public void Line_breaks_and_tabs_become_spaces()
    {
        Assert.Equal("a b c d", EmoteInput.Flatten("a\r\nb\nc\td"));
    }

    [Fact]
    public void Splicing_moves_the_pictures_after_the_change_and_drops_the_replaced_ones()
    {
        var shown = new[] { new EmoteSpan(0, 5), new EmoteSpan(6, 5), new EmoteSpan(12, 8) };
        var (text, caret, kept) = EmoteInput.Splice("Kappa Kappa PogChamp", shown, 6, 11, "hello there", 500);
        Assert.Equal("Kappa hello there PogChamp", text);
        Assert.Equal(17, caret);
        Assert.Equal(new[] { 0, 18 }, kept.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Splicing_never_goes_over_the_length_limit()
    {
        var (text, caret, _) = EmoteInput.Splice("abc", Array.Empty<EmoteSpan>(), 3, 3, "\ndefgh", 6);
        Assert.Equal("abc de", text);
        Assert.Equal(6, caret);
    }

    private static void Type(EmoteTextBox box, string text)
    {
        foreach (var c in text)
        {
            box.Selection.Text = c.ToString();
            box.Selection.Select(box.Selection.End, box.Selection.End);
            box.Refresh();
        }
    }

    private static EmoteTextBox Box() => new() { EmoteUrl = name => Known.Contains(name) ? "http://e/" + name : null };

    private static string Shape(EmoteTextBox box) =>
        string.Join("|", ((Paragraph)box.Document.Blocks.FirstBlock).Inlines.Select(i => i switch
        {
            Run run => "'" + run.Text + "'",
            InlineUIContainer { Tag: string name } => "[" + name + "]",
            _ => "?",
        }));

    [Fact]
    public void The_box_shows_codes_as_pictures_but_its_text_keeps_the_codes()
    {
        Sta.Run(() =>
        {
            var box = Box();
            box.SetText("hi furaSHTUKNOST furaSHTUKNOST", 30);
            Assert.Equal("'hi '|[furaSHTUKNOST]|' '|[furaSHTUKNOST]", Shape(box));
            Assert.Equal("hi furaSHTUKNOST furaSHTUKNOST", box.Text);
            Assert.Equal(30, box.CaretIndex);
        });
    }

    [Fact]
    public void The_caret_counts_a_picture_as_its_code()
    {
        Sta.Run(() =>
        {
            var box = Box();
            box.SetText("a Kappa b", 0);
            foreach (var offset in new[] { 0, 1, 2, 7, 8, 9 })
            {
                box.CaretIndex = offset;
                Assert.Equal(offset, box.CaretIndex);
            }
            box.CaretIndex = 4;
            Assert.Equal(7, box.CaretIndex);
        });
    }

    [Fact]
    public void Typing_a_code_turns_it_into_a_picture_once_a_space_is_typed()
    {
        Sta.Run(() =>
        {
            var box = Box();
            box.SetText("hi ", 3);
            Type(box, "Kappa");
            Assert.Equal("'hi Kappa'", Shape(box));
            Assert.Equal(8, box.CaretIndex);
            Type(box, " ");
            Assert.Equal("'hi '|[Kappa]|' '", Shape(box));
            Assert.Equal("hi Kappa ", box.Text);
            Assert.Equal(9, box.CaretIndex);
        });
    }

    [Fact]
    public void Pasted_and_selected_text_goes_through_the_same_rules()
    {
        Sta.Run(() =>
        {
            var box = Box();
            box.MaxLength = 12;
            box.SetText("Kappa Kappa Kappa", 0);
            Assert.Equal("Kappa Kappa ", box.Text);
            var peer = (IValueProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Value);
            Assert.Equal("Kappa Kappa ", peer.Value);
            peer.SetValue("PogChamp ok");
            Assert.Equal("[PogChamp]|' ok'", Shape(box));
            Assert.Equal(11, box.CaretIndex);
            box.Clear();
            Assert.Equal("", box.Text);
        });
    }
}
