using StreamHelper.Sync;

namespace StreamHelper.Tests;

public class ReleaseNotesTests
{
    private static string Spans(NotesBlock block) =>
        string.Concat(block.Spans.Select(s => s.Bold ? "[" + s.Text + "]" : s.Text));

    [Fact]
    public void Headings_list_items_and_paragraphs_are_told_apart()
    {
        var blocks = ReleaseNotesFormat.Parse("## Что нового\r\n\r\n- **Писать в чат** из оверлея\n* второе\n+ третье\n1. первое по номеру\n2) второе по номеру\n\nПросто текст\n---\n### Мелочи");

        Assert.Equal(
            new[] { NotesBlockKind.Heading, NotesBlockKind.Bullet, NotesBlockKind.Bullet, NotesBlockKind.Bullet, NotesBlockKind.Bullet, NotesBlockKind.Bullet, NotesBlockKind.Paragraph, NotesBlockKind.Heading },
            blocks.Select(b => b.Kind).ToArray());
        Assert.Equal((2, "Что нового"), (blocks[0].Level, Spans(blocks[0])));
        Assert.Equal(("•", "[Писать в чат] из оверлея", true), (blocks[1].Marker, Spans(blocks[1]), blocks[1].GapBefore));
        Assert.Equal(("1.", "первое по номеру"), (blocks[4].Marker, Spans(blocks[4])));
        Assert.Equal("2)", blocks[5].Marker);
        Assert.True(blocks[6].GapBefore);
        Assert.Equal((3, true), (blocks[7].Level, blocks[7].GapBefore));
        Assert.False(blocks[0].GapBefore);
    }

    [Theory]
    [InlineData("**Важно:** после обновления", "[Важно:] после обновления")]
    [InlineData("a __b__ c", "a [b] c")]
    [InlineData("код `x` и [ссылка](https://example.com)", "код x и ссылка")]
    [InlineData("незакрытый **жирный", "незакрытый **жирный")]
    [InlineData("**весь**", "[весь]")]
    [InlineData("", "")]
    public void Bold_links_and_code_marks_become_plain_or_bold_text(string text, string expected) =>
        Assert.Equal(expected, string.Concat(ReleaseNotesFormat.Spans(text).Select(s => s.Bold ? "[" + s.Text + "]" : s.Text)));

    [Fact]
    public void An_empty_or_missing_text_gives_no_blocks()
    {
        Assert.Empty(ReleaseNotesFormat.Parse(""));
        Assert.Empty(ReleaseNotesFormat.Parse(null!));
        Assert.Empty(ReleaseNotesFormat.Parse("\n\n   \n"));
    }

    [Fact]
    public void The_window_gets_the_whole_text_while_the_banner_keeps_a_short_one()
    {
        var body = "**Важно:** строка\r\n\r\n" + string.Join("\r\n", Enumerable.Range(1, 120).Select(i => $"- пункт номер {i} с описанием изменения"));
        var json = "{\"tag_name\":\"v1.0.0.3\",\"name\":\"StreamHelper 1.0.0.3\",\"html_url\":\"https://github.com/x/y/releases/tag/v1.0.0.3\",\"published_at\":\"2026-10-11T00:00:00Z\",\"body\":"
                   + System.Text.Json.JsonSerializer.Serialize(body) + ",\"assets\":[]}";

        var release = AppUpdate.ParseRelease(json)!;

        Assert.True(release.Notes.Length <= AppUpdate.MaxNotesLength + 1);
        Assert.EndsWith("…", release.Notes);
        Assert.StartsWith("Важно: строка", release.Notes);
        Assert.Equal(body.Replace("\r", ""), release.Body);
        Assert.Contains("пункт номер 120", release.Body);
        Assert.Equal(121, ReleaseNotesFormat.Parse(release.Body).Count);
    }

    [Fact]
    public void A_huge_text_is_cut_for_the_window_too()
    {
        var full = AppUpdate.FullNotes(new string('x', AppUpdate.MaxBodyLength + 50));

        Assert.Equal(AppUpdate.MaxBodyLength + 1, full.Length);
        Assert.EndsWith("…", full);
        Assert.Equal("", AppUpdate.FullNotes(null!));
    }
}
