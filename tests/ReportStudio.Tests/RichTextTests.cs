using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Text;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Rich text (requirement 36, first part): the markup reader and the engine.</summary>
public class RichTextTests
{
    private static TextRun Plain(string text) => new(text);
    private static TextRun Bold(string text) => new(text, Bold: true);

    // ---- the markup reader ----

    [Fact]
    public void Text_without_markup_is_one_plain_run()
    {
        Assert.Equal(new[] { Plain("just text") }, RichText.Parse("just text"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_gives_no_runs(string? markup)
    {
        Assert.Empty(RichText.Parse(markup));
    }

    [Fact]
    public void Bold_italic_underline_and_strike_make_runs()
    {
        Assert.Equal(new[] { Plain("a "), Bold("bold"), Plain(" c") }, RichText.Parse("a <b>bold</b> c"));
        Assert.Equal(new[] { new TextRun("x", Italic: true) }, RichText.Parse("<i>x</i>"));
        Assert.Equal(new[] { new TextRun("x", Underline: true) }, RichText.Parse("<u>x</u>"));
        Assert.Equal(new[] { new TextRun("x", Strike: true) }, RichText.Parse("<s>x</s>"));
    }

    [Fact]
    public void The_usual_alternative_names_work_and_case_does_not_matter()
    {
        Assert.Equal(new[] { Bold("x") }, RichText.Parse("<strong>x</strong>"));
        Assert.Equal(new[] { Bold("x") }, RichText.Parse("<B>x</B>"));
        Assert.Equal(new[] { new TextRun("x", Italic: true) }, RichText.Parse("<em>x</em>"));
        Assert.Equal(new[] { new TextRun("x", Underline: true) }, RichText.Parse("<ins>x</ins>"));
        Assert.Equal(new[] { new TextRun("x", Strike: true) }, RichText.Parse("<strike>x</strike>"));
        Assert.Equal(new[] { new TextRun("x", Strike: true) }, RichText.Parse("<del>x</del>"));
    }

    [Fact]
    public void Looks_add_up_when_tags_are_nested()
    {
        Assert.Equal(new[] { new TextRun("x", Bold: true, Italic: true) }, RichText.Parse("<b><i>x</i></b>"));
        Assert.Equal(new[] { Bold("a"), new TextRun("b", Bold: true, Italic: true), Bold("c") }, RichText.Parse("<b>a<i>b</i>c</b>"));
    }

    [Fact]
    public void Neighbouring_runs_with_the_same_look_are_joined()
    {
        Assert.Equal(new[] { Bold("ab") }, RichText.Parse("<b>a</b><b>b</b>"));
        Assert.Equal(new[] { Plain("ab") }, RichText.Parse("a<u></u>b"));
    }

    [Fact]
    public void An_unclosed_tag_lasts_to_the_end_and_a_stray_closing_tag_is_ignored()
    {
        Assert.Equal(new[] { Plain("a "), Bold("bold") }, RichText.Parse("a <b>bold"));
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("</b>x"));
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("<b/>x"));
    }

    [Fact]
    public void Crossed_tags_close_what_they_enclose()
    {
        // </b> closes the italic opened inside it too; the later </i> then has nothing to close.
        Assert.Equal(new[] { new TextRun("x", Bold: true, Italic: true), Plain("y") }, RichText.Parse("<b><i>x</b>y</i>"));
    }

    [Theory]
    [InlineData("a<br>b")]
    [InlineData("a<br/>b")]
    [InlineData("a<br />b")]
    [InlineData("a<BR>b")]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void A_break_is_a_new_line(string markup)
    {
        Assert.Equal(new[] { Plain("a\nb") }, RichText.Parse(markup));
    }

    [Fact]
    public void Paragraphs_end_with_a_new_line_but_never_leave_a_blank_one()
    {
        Assert.Equal("one\ntwo\n", RichText.Plain(RichText.Parse("<p>one</p><p>two</p>")));
        Assert.Equal("one\ntwo\n", RichText.Plain(RichText.Parse("<div>one</div><div>two</div>")));
        Assert.Equal("a\nb", RichText.Plain(RichText.Parse("a<p>b")));
    }

    [Fact]
    public void Font_and_span_set_the_colour()
    {
        Assert.Equal(new[] { new TextRun("x", Color: "#CC0000") }, RichText.Parse("<font color=\"#c00\">x</font>"));
        Assert.Equal(new[] { new TextRun("x", Color: "#FF0000") }, RichText.Parse("<font color='red'>x</font>"));
        Assert.Equal(new[] { new TextRun("x", Color: "#0000FF") }, RichText.Parse("<font color=#0000ff>x</font>"));
        Assert.Equal(new[] { new TextRun("x", Color: "#00FF00") }, RichText.Parse("<span style=\"color:#0f0\">x</span>"));
    }

    [Fact]
    public void A_colour_that_is_not_one_is_ignored()
    {
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("<font color=\"not-a-colour\">x</font>"));
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("<font color=\"url(http://x)\">x</font>"));
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("<span style=\"color:expression(alert(1))\">x</span>"));
    }

    [Fact]
    public void A_span_style_may_set_weight_style_and_decoration_too()
    {
        var runs = RichText.Parse("<span style=\"color:#00f; font-weight:bold; font-style:italic; text-decoration:underline line-through\">x</span>");

        Assert.Equal(new[] { new TextRun("x", Bold: true, Italic: true, Underline: true, Strike: true, Color: "#0000FF") }, runs);
        Assert.Equal(new[] { Bold("x") }, RichText.Parse("<span style=\"font-weight:700\">x</span>"));
        Assert.Equal(new[] { Plain("x") }, RichText.Parse("<span style=\"font-weight:400\">x</span>"));
    }

    [Fact]
    public void The_colour_goes_back_when_the_tag_closes()
    {
        Assert.Equal(new[] { new TextRun("a", Color: "#FF0000"), Plain("b") }, RichText.Parse("<font color=red>a</font>b"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "alert(1)")]
    [InlineData("<a href=\"http://x\">link</a>", "link")]
    [InlineData("<img src=\"x.png\">text", "text")]
    [InlineData("<style>p{}</style>x", "p{}x")]
    [InlineData("<unknown attr=1>kept</unknown>", "kept")]
    public void Other_tags_are_dropped_and_their_text_kept(string markup, string expected)
    {
        Assert.Equal(new[] { Plain(expected) }, RichText.Parse(markup));
    }

    [Theory]
    [InlineData("a < b and c > d")]
    [InlineData("x <3 y")]
    [InlineData("1 <2")]
    [InlineData("a <> b")]
    public void A_less_than_sign_that_starts_no_tag_stays_text(string text)
    {
        Assert.Equal(new[] { Plain(text) }, RichText.Parse(text));
    }

    [Fact]
    public void Entities_are_decoded()
    {
        Assert.Equal(new[] { Plain("<b> & A B   \"q\" 'a'") },
            RichText.Parse("&lt;b&gt; &amp; &#65; &#x42; &nbsp; &quot;q&quot; &apos;a&apos;"));
    }

    [Fact]
    public void An_entity_that_is_not_one_stays_as_written()
    {
        Assert.Equal(new[] { Plain("&foo; &#0; &#xD800; &#99999999; & x") }, RichText.Parse("&foo; &#0; &#xD800; &#99999999; & x"));
    }

    [Fact]
    public void A_decoded_entity_is_text_and_never_a_tag()
    {
        Assert.Equal(new[] { Plain("<b>not bold</b>") }, RichText.Parse("&lt;b&gt;not bold&lt;/b&gt;"));
    }

    [Fact]
    public void An_escaped_value_comes_back_unchanged_and_unstyled()
    {
        var value = "5 < 6 & <b>x</b> &amp; y";

        Assert.Equal(value, RichText.Plain(RichText.Parse(RichText.Escape(value))));
        Assert.False(RichText.IsStyled(RichText.Parse(RichText.Escape(value))));
        Assert.Equal("", RichText.Escape(null));
    }

    [Fact]
    public void Very_long_markup_is_cut()
    {
        var runs = RichText.Parse(new string('a', RichText.MaxLength + 5000));

        Assert.Equal(RichText.MaxLength, RichText.Plain(runs).Length);
    }

    [Fact]
    public void Looking_different_is_told_from_plain()
    {
        Assert.False(RichText.IsStyled(RichText.Parse("plain <br> text")));
        Assert.False(RichText.IsStyled(Array.Empty<TextRun>()));
        Assert.True(RichText.IsStyled(RichText.Parse("<b>x</b>")));
        Assert.True(RichText.IsStyled(RichText.Parse("<font color=red>x</font>")));
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("#aabbcc", "#AABBCC")]
    [InlineData("  #AABBCC ", "#AABBCC")]
    [InlineData("red", "#FF0000")]
    [InlineData("RED", "#FF0000")]
    [InlineData("grey", "#808080")]
    [InlineData("not-a-colour", null)]
    [InlineData("#12", null)]
    [InlineData("#12345", null)]
    [InlineData("#GGGGGG", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Colours_are_read_as_hex_or_a_few_names(string? text, string? expected)
    {
        Assert.Equal(expected, RichText.NormalizeColor(text));
    }

    // ---- the setting ----

    [Fact]
    public void An_item_is_plain_unless_asked_and_the_setting_survives_saving()
    {
        Assert.False(new ReportItem().RichText);

        var def = new ReportDefinition();
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "<b>x</b>", RichText = true });

        Assert.True(ReportDefinition.FromJson(def.ToJson()).Detail.Items[0].RichText);
    }

    [Fact]
    public void An_old_file_without_the_setting_loads_plain()
    {
        var def = ReportDefinition.FromJson("""{ "Name": "Old", "Detail": { "Items": [ { "Kind": "Text", "Text": "<b>x</b>" } ] } }""");

        Assert.False(def.Detail.Items[0].RichText);
    }

    // ---- in a report ----

    private static ReportResult Run(string text, object? value = null, bool rich = true)
    {
        var def = new ReportDefinition { Name = "Rich" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = text, RichText = rich, Width = 200, Height = 20 });
        return ReportEngine.Run(def, null,
            new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = value } },
            new ReportOptions { Culture = CultureInfo.InvariantCulture });
    }

    private static RenderedItem Item(ReportResult r) => r.Pages[0].Items.First(i => i.Kind == ItemKind.Text);

    [Fact]
    public void Rich_text_gives_plain_text_and_runs()
    {
        var item = Item(Run("Total: <b>{A}</b> only", 5));

        Assert.Equal("Total: 5 only", item.Text);
        Assert.Equal(new[] { Plain("Total: "), Bold("5"), Plain(" only") }, item.Runs);
    }

    [Fact]
    public void A_data_value_is_never_read_as_markup()
    {
        var item = Item(Run("<b>{A}</b>", "<i>x</i> & <script>"));

        Assert.Equal("<i>x</i> & <script>", item.Text);
        Assert.Equal(new[] { Bold("<i>x</i> & <script>") }, item.Runs);
    }

    [Fact]
    public void Rich_text_with_no_look_of_its_own_stays_plain()
    {
        var item = Item(Run("Name: {A}<br>second line", "Ravi"));

        Assert.Equal("Name: Ravi\nsecond line", item.Text);
        Assert.Null(item.Runs);
    }

    [Fact]
    public void Without_the_setting_the_text_is_exactly_as_written()
    {
        var item = Item(Run("<b>{A}</b>", 5, rich: false));

        Assert.Equal("<b>5</b>", item.Text);
        Assert.Null(item.Runs);
    }

    [Fact]
    public void A_bad_expression_prints_the_error_text_and_warns()
    {
        var result = Run("<b>{NoSuchField}</b>");

        Assert.Equal("#ERR", Item(result).Text);
        Assert.Null(Item(result).Runs);
        Assert.NotEmpty(result.Warnings);
    }
}
