using ReportStudio.Core.Text;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Line breaking of rich text (requirement 36b). Every character is 10 wide and every line 20 high.</summary>
public class RichLayoutTests
{
    private static List<RichLine> Wrap(double width, bool wordWrap, params TextRun[] runs) =>
        RichLayout.Wrap(runs, width, wordWrap, (_, t) => t.Length * 10.0, _ => 20, 16);

    private static string[] Texts(List<RichLine> lines) => lines.Select(l => string.Concat(l.Pieces.Select(p => p.Text))).ToArray();

    [Fact]
    public void Text_that_fits_stays_on_one_line()
    {
        var lines = Wrap(200, true, new TextRun("hello world"));

        Assert.Equal(new[] { "hello world" }, Texts(lines));
        Assert.Equal(110, lines[0].Width);
        Assert.Equal(20, lines[0].Height);
    }

    [Fact]
    public void A_word_that_does_not_fit_moves_to_the_next_line()
    {
        var lines = Wrap(100, true, new TextRun("hello world"));

        Assert.Equal(new[] { "hello", "world" }, Texts(lines));
    }

    [Fact]
    public void Spaces_at_the_end_of_a_wrapped_line_are_not_counted()
    {
        var lines = Wrap(100, true, new TextRun("hello world"));

        Assert.Equal(50, lines[0].Width);
    }

    [Fact]
    public void A_line_break_starts_a_new_line_and_an_empty_line_has_the_empty_height()
    {
        var lines = Wrap(500, true, new TextRun("a\n\nb"));

        Assert.Equal(new[] { "a", "", "b" }, Texts(lines));
        Assert.Equal(16, lines[1].Height);
    }

    [Fact]
    public void A_closing_line_break_adds_no_extra_line()
    {
        Assert.Equal(new[] { "a" }, Texts(Wrap(500, true, new TextRun("a\n"))));
    }

    [Fact]
    public void A_word_in_several_looks_is_not_split_between_them()
    {
        var lines = Wrap(130, true, new TextRun("see un"), new TextRun("believ", Bold: true), new TextRun("able"));

        Assert.Equal(new[] { "see", "unbelievable" }, Texts(lines));
        Assert.Equal(3, lines[1].Pieces.Count);
    }

    [Fact]
    public void Pieces_keep_their_own_look_and_width()
    {
        var bold = new TextRun("big", Bold: true);
        var lines = Wrap(500, true, new TextRun("a "), bold, new TextRun(" z"));

        var line = Assert.Single(lines);
        Assert.Equal(3, line.Pieces.Count);
        Assert.Same(bold, line.Pieces[1].Run);
        Assert.Equal(30, line.Pieces[1].Width);
        Assert.Equal(70, line.Width);
    }

    [Fact]
    public void A_word_wider_than_the_box_is_broken_by_character()
    {
        var lines = Wrap(30, true, new TextRun("abcdefgh"));

        Assert.Equal(new[] { "abc", "def", "gh" }, Texts(lines));
    }

    [Fact]
    public void Without_word_wrap_a_line_is_never_broken()
    {
        var lines = Wrap(30, false, new TextRun("hello world\nsecond"));

        Assert.Equal(new[] { "hello world", "second" }, Texts(lines));
    }

    [Fact]
    public void Spaces_at_the_start_of_a_line_are_dropped()
    {
        Assert.Equal(new[] { "a" }, Texts(Wrap(500, true, new TextRun("   a"))));
    }

    [Fact]
    public void A_line_is_as_high_as_its_highest_piece()
    {
        var lines = RichLayout.Wrap(new[] { new TextRun("a"), new TextRun("b", Bold: true) }, 500, true,
            (_, t) => t.Length * 10.0, r => r.Bold ? 30 : 20, 16);

        Assert.Equal(30, lines[0].Height);
        Assert.Equal(30, RichLayout.TotalHeight(lines));
    }

    [Fact]
    public void No_text_gives_no_lines()
    {
        Assert.Empty(Wrap(100, true));
    }
}
