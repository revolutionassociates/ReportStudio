using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Web.Tests;

/// <summary>Text measuring without GDI+ (requirement 49): real font widths, the HTML box rules, a fallback when no font is there.</summary>
public class WebTextMeasurerTests
{
    private static WebTextMeasurer Make() => new(TestFiles.FontsFolder());

    private static ReportItem Item(double size = 9, bool bold = false) =>
        new() { Kind = ItemKind.Text, FontSize = size, Bold = bold };

    private const string Sentence = "The quick brown fox jumps over the lazy dog while the invoice total keeps growing every single month";

    [Fact]
    public void The_shipped_fonts_are_loaded()
    {
        Assert.True(Make().FontCount > 0);
    }

    [Fact]
    public void One_short_line_is_one_line_high()
    {
        // 9 pt = 12 px; a line is 1.25 times that, plus 2 pixels
        Assert.Equal(17.0, Make().MeasureHeight("Hello", Item(), 300), 3);
    }

    [Fact]
    public void Empty_text_is_still_one_line()
    {
        Assert.Equal(17.0, Make().MeasureHeight("", Item(), 300), 3);
    }

    [Fact]
    public void Every_line_break_adds_a_line()
    {
        Assert.Equal(47.0, Make().MeasureHeight("a\nb\nc", Item(), 300), 3);
        Assert.Equal(47.0, Make().MeasureHeight("a\r\nb\r\nc", Item(), 300), 3);
    }

    [Fact]
    public void A_narrower_box_makes_the_text_taller()
    {
        var m = Make();
        var wide = m.MeasureHeight(Sentence, Item(), 600);
        var narrow = m.MeasureHeight(Sentence, Item(), 120);
        Assert.True(narrow > wide, $"{narrow} should be more than {wide}");
    }

    [Fact]
    public void A_bigger_font_makes_the_text_taller()
    {
        var m = Make();
        Assert.True(m.MeasureHeight(Sentence, Item(18), 300) > m.MeasureHeight(Sentence, Item(9), 300));
    }

    [Fact]
    public void A_word_wider_than_the_box_breaks_into_more_lines()
    {
        var m = Make();
        var h = m.MeasureHeight(new string('W', 200), Item(), 100);
        Assert.True(h > 17 * 3, $"{h} should be several lines");
    }

    [Fact]
    public void A_box_of_no_width_still_gives_a_finite_height()
    {
        var h = Make().MeasureHeight("abc", Item(), 0);
        Assert.True(double.IsFinite(h) && h > 0);
    }

    [Fact]
    public void A_font_that_is_not_shipped_uses_the_regular_face()
    {
        var m = Make();
        var other = new ReportItem { Kind = ItemKind.Text, FontFamily = "No Such Font", FontSize = 9 };
        Assert.Equal(m.MeasureHeight(Sentence, Item(), 200), m.MeasureHeight(Sentence, other, 200), 3);
    }

    [Fact]
    public void Without_any_font_the_estimate_is_used()
    {
        var none = new WebTextMeasurer(Path.Combine(Path.GetTempPath(), "rs-no-fonts-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(0, none.FontCount);
        Assert.True(none.MeasureHeight(Sentence, Item(), 200) > 0);
    }
}
